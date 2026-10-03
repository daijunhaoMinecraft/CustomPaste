using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CustomPaste.Models;

namespace CustomPaste.Services;

public sealed partial class TranslationService
{
    public async IAsyncEnumerable<string> StreamTranslateAsync(string text, AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (settings.Provider != TranslationProvider.AI) throw new TranslationException("实时翻译仅支持 AI 服务。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > settings.MaxTextLength)
            throw new TranslationException("待翻译文本为空或超过长度限制。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        string key;
        try { key = SecretProtector.Unprotect(settings.ProtectedApiKeys.GetValueOrDefault(settings.Provider, "")); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        { throw new TranslationException("无法解密 AI 密钥，请重新填写。"); }
        using var request = CreateRequest(text, settings, key, true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        CheckResponse(response, settings.Provider);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new TranslationException("AI 服务未返回 SSE 流，请确认接口支持 stream=true，或关闭实时输入。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var state = new AIStreamState(settings.MaxTextLength);
        var totalWireChars = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            totalWireChars += count;
            if (totalWireChars > 16 * 1024 * 1024) throw new TranslationException("AI 流式响应过大，已停止处理。");
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (buffer[i] != '\n')
                {
                    line.Append(buffer[i]);
                    if (line.Length > 256 * 1024) throw new TranslationException("AI 流式事件过大，已停止处理。");
                    continue;
                }
                var value = line.ToString().TrimEnd('\r');
                line.Clear();
                if (value.Length == 0)
                {
                    if (data.Length == 0) continue;
                    var fragment = state.Accept(data.ToString().TrimEnd('\n'));
                    data.Clear();
                    if (!string.IsNullOrEmpty(fragment)) yield return fragment;
                    if (state.Done) yield break;
                }
                else if (value.StartsWith("data:", StringComparison.Ordinal))
                {
                    var field = value[5..];
                    if (field.StartsWith(' ')) field = field[1..];
                    data.Append(field).Append('\n');
                    if (data.Length > 256 * 1024) throw new TranslationException("AI 流式事件过大，已停止处理。");
                }
                // Ignore SSE comments, id, event and retry fields. They are never treated as text.
            }
        }
        throw new TranslationException("AI 流在完成标记前断开，已停止输入。已输入的部分不会撤回。");
    }
}

internal sealed class AIStreamState(int maxLength)
{
    private bool _stopped;
    private int _length;
    public bool Done { get; private set; }
    public string? Accept(string data)
    {
        if (Done) throw new TranslationException("AI 流在结束后仍返回内容。");
        if (data == "[DONE]")
        {
            if (!_stopped || _length == 0) throw new TranslationException("AI 流未正常完成或没有返回译文。");
            Done = true;
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out _)) throw new TranslationException("AI 流返回服务错误，已停止输入。");
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null; // Optional usage-only event.
            var choice = choices[0];
            if (choice.TryGetProperty("index", out var index) && index.GetInt32() != 0)
                throw new TranslationException("AI 返回了非预期的多候选流。");
            var delta = choice.GetProperty("delta");
            if (delta.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null &&
                (refusal.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(refusal.GetString())))
                throw new TranslationException("AI 拒绝继续翻译，已停止输入。");
            if ((delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null &&
                 (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0)) ||
                (delta.TryGetProperty("function_call", out var function) && function.ValueKind != JsonValueKind.Null))
                throw new TranslationException("AI 返回工具调用，已停止输入。");
            var content = delta.TryGetProperty("content", out var value) && value.ValueKind != JsonValueKind.Null
                ? value.GetString() : null;
            if (_stopped && !string.IsNullOrEmpty(content)) throw new TranslationException("AI 在完成状态后返回额外内容。");
            var finish = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null
                ? reason.GetString() : null;
            if (finish is not null && finish != "stop") throw new TranslationException(finish == "length"
                ? "AI 译文被截断，已停止输入。" : "AI 未正常完成翻译，已停止输入。");
            if (finish == "stop") _stopped = true;
            _length += content?.Length ?? 0;
            if (_length > maxLength) throw new TranslationException("AI 译文超过单次文本上限，已停止输入。");
            return content; // Never return reasoning_content or any other fields.
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        { throw new TranslationException("AI 流式响应格式无效，已停止输入。"); }
    }
}

internal sealed class StreamingTextBuffer
{
    private string _pending = "";
    public string Push(string text, bool final = false)
    {
        text = _pending + text;
        _pending = "";
        if (!final && text.Length > 0 && (text[^1] == '\r' || char.IsHighSurrogate(text[^1])))
        { _pending = text[^1..]; text = text[..^1]; }
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
