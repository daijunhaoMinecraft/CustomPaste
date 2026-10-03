using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using CustomPaste.Models;
using CustomPaste.Services;
using Forms = System.Windows.Forms;

namespace CustomPaste;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _cancelItem;
    private SettingsWindow? _window;
    private HotkeyService? _hotkeys;
    private HttpClient? _http;
    private Task _pasteTask = Task.CompletedTask;
    public bool IsExiting { get; private set; }
    public AppSettings Settings { get; private set; } = new();
    public StorageService Storage { get; private set; } = null!;
    public LogService Log { get; private set; } = null!;
    public HistoryService History { get; private set; } = null!;
    public TranslationService Translator { get; private set; } = null!;
    public PasteService Paste { get; private set; } = null!;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            _instanceMutex = new Mutex(true, @"Local\CustomPaste." + sid, out _ownsMutex);
            if (!_ownsMutex)
            {
                MessageBox.Show("CustomPaste 已在运行，请从系统托盘打开。", "CustomPaste", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            Storage = new StorageService();
            Settings = Storage.LoadSettings();
            Log = new LogService(Storage.Root, Dispatcher);
            if (Storage.LoadWarning is not null) Log.Write("WARN", "SettingsLoad", Storage.LoadWarning);
            History = new HistoryService(Storage);
            try
            {
                if (Settings.HistoryEnabled) History.Load(Settings.HistoryLimit);
                else History.Clear();
            }
            catch (Exception ex)
            {
                Log.Write("ERROR", "HistoryLoad", $"历史记录无法读取或清理（{ex.GetType().Name}），原文件保留以便排查。");
            }

            // Disable redirects so subscription keys cannot be forwarded to another host.
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
            Translator = new TranslationService(_http);
            Paste = new PasteService(Translator, History, Log);
            Paste.Notice += Notify;
            Paste.StatusChanged += () =>
            {
                if (_cancelItem is not null) _cancelItem.Enabled = Paste.IsBusy;
            };
            _hotkeys = new HotkeyService();
            _hotkeys.Pressed += OnHotkey;
            string? hotkeyError = null;
            try
            {
                _hotkeys.Apply(Settings.GetHotkeys());
            }
            catch (InvalidOperationException ex)
            {
                hotkeyError = ex.Message;
                Log.Write("ERROR", "HotkeyRegistration", hotkeyError);
            }

            CreateTray();
            _window = new SettingsWindow(this);
            MainWindow = _window;
            if (!e.Args.Contains("--background") || hotkeyError is not null) _window.ShowPage();
            if (hotkeyError is not null)
            {
                _window.ShowFeedback(hotkeyError);
                Notify(hotkeyError);
            }
            else if (Storage.LoadWarning is not null) _window.ShowFeedback(Storage.LoadWarning);

            DispatcherUnhandledException += OnUnhandledException;
            Log.Write("INFO", "Startup", "CustomPaste 已启动；关闭窗口后在托盘运行。");
        }
        catch (Exception ex)
        {
            Log?.Write("ERROR", "Startup", $"启动失败（{ex.GetType().Name}）。");
            MessageBox.Show($"启动失败（{ex.GetType().Name}）。请检查 .NET 8 桌面运行时和本地数据目录权限。", "CustomPaste", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 CustomPaste", null, (_, _) => _window?.ShowPage());
        menu.Items.Add("粘贴历史", null, (_, _) => _window?.ShowPage(3));
        menu.Items.Add("运行日志", null, (_, _) => _window?.ShowPage(4));
        _cancelItem = new Forms.ToolStripMenuItem("取消当前任务", null, (_, _) => Paste.Cancel()) { Enabled = false };
        menu.Items.Add(_cancelItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "CustomPaste · 自定义粘贴",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => _window?.ShowPage();
        _notifyIcon.BalloonTipClicked += (_, _) => _window?.ShowPage(4);
    }

    private void OnHotkey(PasteAction action)
    {
        if (IsExiting) return;
        if (action == PasteAction.Cancel)
        {
            // Cancellation must bypass the single-flight guard and preserve the active task for shutdown.
            Paste.Cancel();
            _window?.CancelTest();
            return;
        }
        if (Paste.IsBusy)
        {
            Log.Write("WARN", "Busy", "忽略重复触发，当前任务仍在进行。");
            return;
        }

        _pasteTask = Paste.ExecuteAsync(action, Settings.Clone());
    }

    private void Notify(string message)
    {
        _window?.ShowFeedback(message);
        if (Settings.NotificationsEnabled && !IsExiting)
            _notifyIcon?.ShowBalloonTip(4000, "CustomPaste", message, Forms.ToolTipIcon.Info);
    }

    public void ApplySettings(AppSettings next)
    {
        if (Paste.IsBusy) throw new InvalidOperationException("请等待粘贴结束后再保存设置。");
        next.Validate();
        var previous = Settings;
        _hotkeys!.Apply(next.GetHotkeys());
        try
        {
            Storage.SaveSettings(next);
        }
        catch
        {
            try
            {
                _hotkeys.Apply(previous.GetHotkeys());
            }
            catch (Exception)
            {
                Log.Write("ERROR", "HotkeyRollback", "回滚快捷键失败，请重新保存或重启应用。");
            }

            throw;
        }

        Settings = next;
        try
        {
            if (!next.HistoryEnabled) History.Clear();
            else History.Trim(next.HistoryLimit);
        }
        catch (Exception)
        {
            Log.Write("ERROR", "HistorySettings", "设置已保存，但已有历史记录的清理失败。请检查数据目录权限。");
            throw new InvalidOperationException("设置已保存，但历史记录清理失败，请检查数据目录权限后再次保存。");
        }

        Log.Write("INFO", "Settings", "设置已保存，快捷键已更新。");
    }

    private async void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Exit rather than pretending potentially corrupted UI state is safe to continue using.
        e.Handled = true;
        Log.Write("ERROR", "Unhandled", $"发生未处理异常（{e.Exception.GetType().Name}），应用将安全退出。");
        MessageBox.Show("发生未预期错误，应用将退出。详情见本地日志。", "CustomPaste", MessageBoxButton.OK, MessageBoxImage.Error);
        await ExitAsync();
    }

    public async Task ExitAsync()
    {
        if (IsExiting) return;
        IsExiting = true;
        _window?.CancelTest();
        Paste?.Cancel();
        await _pasteTask;
        _window?.FlushAutoSave();
        _hotkeys?.Dispose();
        _hotkeys = null;
        Log?.Write("INFO", "Shutdown", "应用退出，已释放快捷键与托盘资源。");
        if (Log is not null) await Log.DisposeAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _http?.Dispose();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
        }

        if (Log is not null) Log.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}