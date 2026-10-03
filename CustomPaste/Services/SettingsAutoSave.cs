using System.Windows.Threading;

namespace CustomPaste.Services;

internal sealed class SettingsAutoSave : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _canSave;
    private readonly Action _save;
    public bool Pending { get; private set; }
    public SettingsAutoSave(Dispatcher dispatcher, Func<bool> canSave, Action save, int delayMs = 500)
    {
        _canSave = canSave;
        _save = save;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _timer.Tick += Tick;
    }
    private void Tick(object? sender, EventArgs e) => Flush();
    public void Request() { Pending = true; _timer.Stop(); _timer.Start(); }
    public void Cancel() { _timer.Stop(); Pending = false; }
    public void ResumeIfPending() { if (Pending) { _timer.Stop(); _timer.Start(); } }
    public void Flush()
    {
        _timer.Stop();
        if (!Pending || !_canSave()) return;
        Pending = false;
        _save();
    }
    public void Dispose() { Cancel(); _timer.Tick -= Tick; }
}
