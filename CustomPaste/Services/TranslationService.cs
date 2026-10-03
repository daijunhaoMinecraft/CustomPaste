using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CustomPaste.Models;

namespace CustomPaste.Services;

public sealed class TranslationException : Exception
{
    public TranslationException(string message) : base(message)
    {
    }
}

public sealed partial class TranslationService
{
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public TranslationService(HttpClient client) => _client = client;

    public async Task<string> TranslateAsync(string text, AppSettings settings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.Provider == TranslationProvider.AI ? 90 : 30));
        cancellationToken = timeout.Token;
        settings.Validate();
        if (string.IsNullOrWhiteSpace(text)) throw new TranslationException("没有可翻译的文本。");
        if (text.Length > settings.MaxTextLength) throw new TranslationException("文本超过单次长度上限。");
        string key;
        try
        {
            key = SecretProtector.Unprotect(settings.ProtectedApiKeys.GetValueOrDefault(settings.Provider, ""));
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        {
            throw new TranslationException("无法解密 API Key，请在当前 Windows 账户下重新输入并保存。");
        }

        if (settings.Provider is not (TranslationProvider.DeepLX or TranslationProvider.AI) && string.IsNullOrWhiteSpace(key)) throw new TranslationException("请先为当前翻译服务填写 API Key 并保存设置。");
        using var request = CreateRequest(text, settings, key);
        using var response =
            await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        CheckResponse(response, settings.Provider);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken)) != 0)
        {
            if (buffer.Length + read > 1024 * 1024) throw new TranslationException("翻译响应过大，已停止处理。");
            buffer.Write(bytes, 0, read);
        }

        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            string? translated;
            if (settings.Provider == TranslationProvider.AI)
            {
                translated = AITranslationProtocol.ReadResult(json.RootElement);
            }
            else if (settings.Provider == TranslationProvider.DeepLX)
            {
                var root = json.RootElement;
                var code = root.GetProperty("code").GetInt32();
                if (code != 200) throw new TranslationException(code switch
                {
                    401 or 403 => "DeepLX 认证失败，请检查接口地址中的令牌或 Bearer Token。",
                    429 => "DeepLX 接口或其上游服务限流（业务状态码 429），请稍后重试。",
                    400 => "DeepLX 拒绝了翻译参数，请检查语言和服务版本。",
                    _ => $"DeepLX 翻译失败（业务状态码 {code}）。"
                });
                translated = root.GetProperty("data").GetString();
            }
            else
            {
                translated = settings.Provider == TranslationProvider.Microsoft
                    ? json.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString()
                    : json.RootElement.GetProperty("translations")[0].GetProperty("text").GetString();
            }
            if (string.IsNullOrWhiteSpace(translated)) throw new TranslationException("翻译服务返回了空内容，未执行粘贴。");
            if (translated.Length > settings.MaxTextLength) throw new TranslationException("译文超过单次长度上限，未执行粘贴。");
            return translated;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException
                                      or InvalidOperationException or ArgumentOutOfRangeException or FormatException)
        {
            throw new TranslationException("翻译服务响应格式无效，未执行粘贴。");
        }
    }

    private static void CheckResponse(HttpResponseMessage response, TranslationProvider provider)
    {
        if (!response.IsSuccessStatusCode)
            throw new TranslationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "翻译认证失败，请检查 API Key、订阅类型和区域。",
                HttpStatusCode.TooManyRequests => RateLimitMessage(response, provider),
                (HttpStatusCode)456 => "DeepL 翻译额度已用尽。",
                HttpStatusCode.BadRequest => "翻译参数被拒绝，请检查语言、区域及服务配置。",
                HttpStatusCode.RequestEntityTooLarge => "翻译文本超过服务商的请求大小限制。",
                _ => $"翻译服务暂不可用（HTTP {(int)response.StatusCode}）。"
            });
    }

    private static string RateLimitMessage(HttpResponseMessage response, TranslationProvider provider)
    {
        var message = provider == TranslationProvider.DeepLX
            ? "DeepLX 接口或其上游服务限流（HTTP 429）。这不一定是地址填写错误，请稍后重试。"
            : "翻译服务返回限流（HTTP 429），请稍后重试或检查服务额度。";
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        if (delay is { TotalSeconds: > 0 and <= 86400 })
            message += $"服务建议至少等待 {Math.Ceiling(delay.Value.TotalSeconds):0} 秒。";
        return message;
    }

    private static string DeepLXLanguage(string language) => language == "zh-Hans" ? "ZH" : language.ToUpperInvariant();

    private static Uri GetDeepLXEndpoint(AppSettings settings)
    {
        try
        {
            var endpoint = SecretProtector.Unprotect(settings.ProtectedDeepLXEndpoint);
            if (string.IsNullOrWhiteSpace(endpoint)) throw new TranslationException("请先填写 DeepLX 的完整翻译接口地址。");
            return ParseDeepLXEndpoint(endpoint);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        { throw new TranslationException("无法解密 DeepLX 接口地址，请在当前 Windows 账户下重新填写。"); }
        catch (ArgumentException)
        { throw new TranslationException("DeepLX 接口地址无效，请使用完整 HTTPS 地址；仅本机回环地址允许 HTTP。"); }
    }

    internal static Uri ParseDeepLXEndpoint(string endpoint)
    {
        // URL paths / queries may contain credentials: never put the supplied URL into an exception message.
        if (endpoint.Length > 4096 || endpoint.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("DeepLX 接口地址无效，请使用完整 HTTPS 地址；仅本机回环地址允许 HTTP，且不能包含用户名、密码、空白或片段。");
        return uri;
    }

    private static HttpRequestMessage CreateRequest(string text, AppSettings settings, string key, bool aiStream = false)
    {
        HttpRequestMessage request;
        object payload;
        if (settings.Provider == TranslationProvider.AI)
        {
            (request, payload) = AITranslationProtocol.CreateRequest(text, settings, key, aiStream);
        }
        else if (settings.Provider == TranslationProvider.DeepLX)
        {
            var endpoint = GetDeepLXEndpoint(settings);
            request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(key))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            payload = new
            {
                text,
                source_lang = settings.SourceLanguage == "auto" ? "auto" : DeepLXLanguage(settings.SourceLanguage),
                target_lang = DeepLXLanguage(settings.TargetLanguage)
            };
        }
        else if (settings.Provider == TranslationProvider.Microsoft)
        {
            var url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" +
                      Uri.EscapeDataString(settings.TargetLanguage);
            if (settings.SourceLanguage != "auto") url += "&from=" + Uri.EscapeDataString(settings.SourceLanguage);
            request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
            if (!string.IsNullOrWhiteSpace(settings.MicrosoftRegion))
                request.Headers.Add("Ocp-Apim-Subscription-Region", settings.MicrosoftRegion);
            payload = new[] { new { Text = text } };
        }
        else
        {
            var host = settings.Provider == TranslationProvider.DeepLFree ? "api-free.deepl.com" : "api.deepl.com";
            request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v2/translate");
            request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
            var body = new Dictionary<string, object>
            { ["text"] = new[] { text }, ["target_lang"] = settings.TargetLanguage.ToUpperInvariant() };
            if (settings.SourceLanguage != "auto")
                body["source_lang"] = settings.SourceLanguage.StartsWith("zh", StringComparison.Ordinal)
                    ? "ZH"
                    : settings.SourceLanguage.ToUpperInvariant();
            payload = body;
        }

        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8,
            "application/json");
        return request;
    }
}