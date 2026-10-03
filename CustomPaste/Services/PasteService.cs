using System.Diagnostics;
using CustomPaste.Models;

namespace CustomPaste.Services;

public sealed class PasteService
{
    private readonly IPastePlatform _platform;
    private readonly TranslationService _translator;
    private readonly HistoryService _history;
    private readonly LogService _log;
    private CancellationTokenSource? _operation;
    public bool IsBusy => _operation is not null;
    public string Status { get; private set; } = "准备就绪";
    public event Action? StatusChanged;
    public event Action<string>? Notice;

    public PasteService(TranslationService translator, HistoryService history, LogService log,
        IPastePlatform? platform = null)
    {
        _translator = translator;
        _history = history;
        _log = log;
        _platform = platform ?? new WindowsPastePlatform(log, message => Notice?.Invoke(message));
    }

    public void Cancel() => _operation?.Cancel();

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    public async Task ExecuteAsync(PasteAction action, AppSettings settings)
    {
        if (action == PasteAction.Cancel) { Cancel(); return; }
        if (IsBusy)
        {
            _log.Write("WARN", "Busy", "已有粘贴任务进行中，忽略重复触发。");
            return;
        }

        var target = _platform.GetTarget();
        if (target == IntPtr.Zero)
        {
            Notice?.Invoke("请先切换到目标应用的输入框，再按快捷键。");
            return;
        }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        var token = operation.Token;
        using var monitor = _platform.WatchCancellation(operation.Cancel);
        var stopwatch = Stopwatch.StartNew();
        var translate = action == PasteAction.Translate || action == PasteAction.Custom && settings.TranslateOnPaste;
        var stream = action == PasteAction.Stream || action == PasteAction.Custom && settings.StreamOnPaste;
        var realtime = translate && settings.Provider == TranslationProvider.AI && settings.AIStreamEnabled;
        var mode = realtime ? "AI 实时输入" : translate ? stream ? "翻译 · 输入" : "翻译粘贴" : stream ? "流式输入" : "纯文本粘贴";
        try
        {
            settings.Validate();
            SetStatus("读取剪贴板…");
            var text = await _platform.ReadTextAsync(token);
            if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("剪贴板中没有文本，未执行粘贴。");
            if (text.Length > settings.MaxTextLength)
                throw new InvalidOperationException($"文本超过 {settings.MaxTextLength:N0} 字符上限，未执行粘贴。");
            var sourceLength = text.Length;
            _log.Write("INFO", "PasteStarted", $"{mode}；源文本 {sourceLength} 字符。");
            if (realtime)
            {
                SetStatus("AI 正在生成并输入 · 按 Esc 取消");
                await _platform.WaitForReleaseAsync(target, token);
                var result = new System.Text.StringBuilder();
                var inputBuffer = new StreamingTextBuffer();
                var fragmentCount = 0;
                await foreach (var fragment in _translator.StreamTranslateAsync(text, settings, token))
                {
                    result.Append(fragment);
                    var ready = inputBuffer.Push(fragment);
                    if (ready.Length > 0) await _platform.TypeTextAsync(ready, target, 0, token);
                    // Buffered tiny deltas must still give the dispatcher a chance to process Esc.
                    if (++fragmentCount % 16 == 0) await Task.Delay(1, token);
                }
                var tail = inputBuffer.Push("", final: true);
                if (tail.Length > 0) await _platform.TypeTextAsync(tail, target, 0, token);
                text = result.ToString();
            }
            else
            {
                if (translate)
                {
                    SetStatus("正在翻译 · 按 Esc 取消");
                    text = await _translator.TranslateAsync(text, settings, token);
                }

                await _platform.WaitForReleaseAsync(target, token);
                if (stream)
                {
                    SetStatus("正在输入 · 按 Esc 取消");
                    await _platform.TypeTextAsync(text, target, settings.CharacterDelayMs, token);
                }
                else
                {
                    SetStatus("正在粘贴…");
                    await _platform.PasteClipboardAsync(text, target, settings, token);
                }

            }

            SetStatus("输入已发送");
            _log.Write("INFO", "PasteCompleted",
                $"{mode}；输出 {text.Length} 字符；用时 {stopwatch.ElapsedMilliseconds} ms。输入已发送，目标应用接收情况请自行确认。");
            if (settings.HistoryEnabled)
            {
                try
                {
                    _history.Add(mode, text, sourceLength, settings.HistoryLimit);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                              or System.Security.Cryptography.CryptographicException)
                {
                    _log.Write("ERROR", "History", "输入已发送，但历史记录写入失败。");
                    Notice?.Invoke("输入已发送，但历史记录写入失败，请查看日志。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            var message = token.IsCancellationRequested ? "操作已取消；已输入的部分不会撤回。" : "翻译请求超时，请检查网络后重试。";
            SetStatus(token.IsCancellationRequested ? "已取消" : "请求超时");
            _log.Write("WARN", "PasteCancelled", message);
            Notice?.Invoke(message);
        }
        catch (Exception e)
        {
            var message = e is TranslationException or InvalidOperationException ? e.Message :
                e is System.Net.Http.HttpRequestException ? "无法连接翻译服务，请检查网络。" : "粘贴失败，请检查剪贴板、目标窗口及权限。";
            if (realtime) message += " 如已输入部分文字，这些内容不会自动撤回。";
            SetStatus("操作未完成");
            _log.Write("ERROR", "PasteFailed", $"{message}（{e.GetType().Name}）");
            Notice?.Invoke(message);
        }
        finally
        {
            _operation = null;
            StatusChanged?.Invoke();
        }
    }
}