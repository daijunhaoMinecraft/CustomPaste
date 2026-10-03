using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CustomPaste.Models;

namespace CustomPaste.Services;

// The OS boundary is injectable so orchestration can be tested without typing into the user's desktop.
public interface IPastePlatform
{
    IntPtr GetTarget();
    IDisposable WatchCancellation(Action cancel);
    Task<string> ReadTextAsync(CancellationToken token);
    Task WaitForReleaseAsync(IntPtr target, CancellationToken token);
    Task TypeTextAsync(string text, IntPtr target, int delay, CancellationToken token);
    Task PasteClipboardAsync(string text, IntPtr target, AppSettings settings, CancellationToken token);
}

internal sealed class WindowsPastePlatform(LogService log, Action<string> notice) : IPastePlatform
{
    private readonly LogService _log = log;
    private readonly Action<string> _notice = notice;
    public IntPtr GetTarget()
    {
        var target = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(target, out var pid);
        return pid == Environment.ProcessId ? IntPtr.Zero : target;
    }
    public IDisposable WatchCancellation(Action cancel) => new EscapeMonitor(cancel);
    private sealed class EscapeMonitor : IDisposable
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
        public EscapeMonitor(Action cancel)
        {
            _timer.Tick += (_, _) => { if (NativeMethods.IsDown(0x1B)) cancel(); };
            _timer.Start();
        }
        public void Dispose() => _timer.Stop();
    }
    public Task<string> ReadTextAsync(CancellationToken token) =>
        ClipboardService.RetryAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : "", token);
    private static void CheckTarget(IntPtr target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (NativeMethods.GetForegroundWindow() != target)
            throw new InvalidOperationException("前台窗口已变化，已停止输入以避免粘贴到其他应用。已输入部分不会撤回。");
    }
    public async Task WaitForReleaseAsync(IntPtr target, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            CheckTarget(target, token);
            if (!NativeMethods.ModifiersDown()) return;
            if (timeout.ElapsedMilliseconds > 3000) throw new InvalidOperationException("请松开快捷键后重试。");
            await Task.Delay(15, token);
        } while (true);
    }
    public async Task TypeTextAsync(string text, IntPtr target, int delay, CancellationToken token)
    {
        var count = 0;
        foreach (var inputs in BuildInputBatches(text))
        {
            CheckTarget(target, token);
            if (NativeMethods.ModifiersDown()) throw new InvalidOperationException("检测到修饰键按下，已停止流式输入。");
            NativeMethods.Send(inputs);
            if (delay > 0) await Task.Delay(delay, token);
            else if (++count % 32 == 0) await Task.Delay(1, token);
        }
    }
    internal static IEnumerable<NativeMethods.INPUT[]> BuildInputBatches(string text)
    {
        // Keep surrogate pairs and combining marks together; normalize CRLF so Enter is not sent twice.
        var elements = StringInfo.GetTextElementEnumerator(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        while (elements.MoveNext())
        {
            var inputs = new List<NativeMethods.INPUT>();
            foreach (var c in elements.GetTextElement())
            {
                var control = c is '\n' or '\t';
                var key = c == '\n' ? (ushort)0x0D : c == '\t' ? (ushort)0x09 : c;
                inputs.Add(NativeMethods.Key(key, unicode: !control));
                inputs.Add(NativeMethods.Key(key, up: true, unicode: !control));
            }
            yield return inputs.ToArray();
        }
    }
    public async Task PasteClipboardAsync(string text, IntPtr target, AppSettings settings, CancellationToken token)
    {
        IDataObject? original = null;
        uint sequence = 0;
        var written = false;
        try
        {
            await ClipboardService.RetryAsync(() =>
            {
                CheckTarget(target, token);
                original = settings.RestoreClipboard ? ClipboardService.CaptureSnapshot() : null;
                Clipboard.SetText(text);
                sequence = NativeMethods.GetClipboardSequenceNumber();
                written = true;
                return true;
            }, token);
            CheckTarget(target, token);
            try
            {
                NativeMethods.Send(new[] { NativeMethods.Key(0x11), NativeMethods.Key(0x56), NativeMethods.Key(0x56, true), NativeMethods.Key(0x11, true) });
            }
            finally
            {
                // Best-effort release if SendInput inserted only part of the batch.
                NativeMethods.SendInput(2, new[] { NativeMethods.Key(0x56, true), NativeMethods.Key(0x11, true) }, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
            }
            // Do not shorten this grace period on cancellation: the target may read the clipboard asynchronously.
            await Task.Delay(settings.ClipboardRestoreDelayMs);
        }
        finally
        {
            if (written && settings.RestoreClipboard)
            {
                try
                {
                    await ClipboardService.RetryAsync(() =>
                    {
                        // Preserve a new copy made by the user while the operation was running.
                        if (NativeMethods.GetClipboardSequenceNumber() == sequence)
                        {
                            if (original is not null) Clipboard.SetDataObject(original, true);
                            else Clipboard.Clear();
                        }
                        return true;
                    });
                }
                catch (Exception)
                { _log.Write("WARN", "ClipboardRestore", "无法恢复原剪贴板，当前剪贴板保留粘贴文本。"); _notice("原剪贴板恢复失败，当前保留粘贴文本。"); }
            }
        }
    }
}
