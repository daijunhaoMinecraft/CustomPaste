using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CustomPaste.Models;
using CustomPaste.Services;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;

namespace CustomPaste;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly SettingsAutoSave? _autoSave;
    private bool _savingSettings;
    private AppSettings _draft = new();
    private bool _loading;
    private bool _endpointDirty;
    private bool _aiEndpointDirty;
    private TranslationProvider _editingProvider;
    private CancellationTokenSource? _testCancellation;
    private ICollectionView? _historyView;
    private ICollectionView? _logView;
    public SettingsWindow(App app, bool autoSaveEnabled = true)
    {
        _app = app;
        InitializeComponent();
        SourceBox.ItemsSource = Languages.All;
        TargetBox.ItemsSource = Languages.All.Where(l => l.Code != "auto");
        _historyView = CollectionViewSource.GetDefaultView(app.History.Entries);
        _historyView.Filter = item => item is HistoryEntry entry &&
            (entry.Text.Contains(HistorySearch.Text, StringComparison.OrdinalIgnoreCase) || entry.Mode.Contains(HistorySearch.Text, StringComparison.OrdinalIgnoreCase));
        HistoryList.ItemsSource = _historyView;
        app.History.Entries.CollectionChanged += (_, _) => RefreshHistory();
        _logView = CollectionViewSource.GetDefaultView(app.Log.Entries);
        _logView.Filter = item => item is LogEntry entry &&
            (LogLevel.SelectedIndex == 0 || entry.Level == (LogLevel.SelectedItem as ComboBoxItem)?.Content.ToString()) &&
            (entry.Message.Contains(LogSearch.Text, StringComparison.OrdinalIgnoreCase) || entry.Event.Contains(LogSearch.Text, StringComparison.OrdinalIgnoreCase));
        LogGrid.ItemsSource = _logView;
        app.Paste.StatusChanged += UpdateRuntime;
        LoadDraft();
        Navigation.SelectedIndex = 0;
        Closing += OnClosing;
        if (autoSaveEnabled) _autoSave = new SettingsAutoSave(Dispatcher, () => !_app.Paste.IsBusy, SaveChanges);
        AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(BoundTextChanged));
        UpdateRuntime();
    }

    private void LoadDraft()
    {
        _autoSave?.Cancel();
        _loading = true;
        _draft.PropertyChanged -= DraftChanged;
        _draft = _app.Settings.Clone();
        _draft.PropertyChanged += DraftChanged;
        DataContext = _draft;
        ProviderBox.SelectedValue = _draft.Provider.ToString();
        _editingProvider = _draft.Provider;
        LoadKey();
        _loading = false;
        ActiveHotkey.Text = string.IsNullOrEmpty(_app.Settings.CustomHotkey) ? "尚未设置快捷键" : _app.Settings.CustomHotkey.Replace("+", " + ");
        ApplyAppearance();
        RefreshHistory();
    }
    public void ApplyAppearance()
    {
        var settings = _app.Settings;
        ThemeManager.Current.ApplicationTheme = settings.Theme switch { "Light" => ApplicationTheme.Light, "Dark" => ApplicationTheme.Dark, _ => null };
        ThemeManager.Current.AccentColor = settings.Accent == "System" ? null : (Color)ColorConverter.ConvertFromString(settings.Accent);
        WindowHelper.SetSystemBackdropType(this, settings.Backdrop switch { "Mica" => BackdropType.Mica, "Tabbed" => BackdropType.Tabbed, _ => BackdropType.None });
    }
    public void ShowPage(int index = 0) { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Navigation.SelectedIndex = index; Activate(); }
    public void ShowFeedback(string message) => Feedback.Text = message;
    public void CancelTest() => _testCancellation?.Cancel();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.IsExiting) return;
        e.Cancel = true;
        FlushAutoSave();
        _testCancellation?.Cancel();
        Hide();
    }
    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomePanel is null || e.Source != Navigation || Navigation.SelectedIndex < 0) return;
        var panels = new FrameworkElement[] { HomePanel, TranslationPanel, HotkeysPanel, HistoryPanel, LogsPanel, AppearancePanel };
        for (var i = 0; i < panels.Length; i++) panels[i].Visibility = i == Navigation.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = new[] { "自定义粘贴", "翻译服务", "快捷键", "粘贴历史", "运行日志", "外观与通用" }[Navigation.SelectedIndex];
        PageSubtitle.Text = new[] { "一次快捷键，完成你的文本工作流。", "连接你信任的服务，让语言不再成为障碍。", "让每一种粘贴方式，都触手可及。", "找回曾经粘贴的文字。", "每一次操作，都有迹可循。", "让工具融入你的工作环境。" }[Navigation.SelectedIndex];
    }
    private void DraftChanged(object? sender, PropertyChangedEventArgs e) => RequestAutoSave();
    private void BoundTextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox box && ReferenceEquals(box.GetBindingExpression(TextBox.TextProperty)?.DataItem, _draft))
            RequestAutoSave();
    }
    private void RequestAutoSave()
    {
        if (_loading || _savingSettings || _autoSave is null) return;
        _autoSave.Request();
        ShowFeedback(_app.Paste.IsBusy ? "更改待保存，当前粘贴结束后自动生效。" : "更改待保存…");
    }
    public void FlushAutoSave() => _autoSave?.Flush();
    private void Save_Click(object sender, RoutedEventArgs e) { _autoSave?.Cancel(); SaveChanges(); }
    private void SaveChanges()
    {
        if (_app.Paste.IsBusy) { RequestAutoSave(); return; }
        if (HasValidationErrors(this)) { ShowFeedback("尚未保存：请修正数字输入框中的无效值。"); return; }
        _savingSettings = true;
        try
        {
            SaveKey();
            _draft.Validate();
            if (_app.Settings.HistoryEnabled && !_draft.HistoryEnabled && _app.History.Entries.Count > 0 &&
                MessageBox.Show(this, "关闭历史记录会同时永久清空已有记录，是否继续？", "关闭历史记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                _draft.HistoryEnabled = true;
            if (System.Text.Json.JsonSerializer.Serialize(_draft) != System.Text.Json.JsonSerializer.Serialize(_app.Settings))
                _app.ApplySettings(_draft.Clone());
            ApplyAppearance();
            ActiveHotkey.Text = string.IsNullOrEmpty(_app.Settings.CustomHotkey) ? "尚未设置快捷键" : _app.Settings.CustomHotkey.Replace("+", " + ");
            RefreshHistory();
            ShowFeedback("设置已自动保存并生效。");
        }
        catch (Exception ex)
        {
            _app.Log.Write("ERROR", "Settings", $"设置保存失败（{ex.GetType().Name}）。");
            ShowFeedback("尚未保存：" + SafeSettingsError(ex));
        }
        finally { _savingSettings = false; }
    }
    private static string SafeSettingsError(Exception ex) => ex is ArgumentException or InvalidOperationException ? ex.Message : "无法保存配置，请检查数据目录权限、磁盘空间或密钥加密状态。";
    private static bool HasValidationErrors(DependencyObject obj)
    {
        if (Validation.GetHasError(obj)) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            if (HasValidationErrors(VisualTreeHelper.GetChild(obj, i))) return true;
        return false;
    }
    private void Reload_Click(object sender, RoutedEventArgs e) { LoadDraft(); ShowFeedback("已放弃未保存的更改。"); }
    private void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ApiKeyBox is null || ProviderBox.SelectedValue is not string name) return;
        try
        {
            SaveKey();
            _draft.Provider = Enum.Parse<TranslationProvider>(name);
            _editingProvider = _draft.Provider;
            LoadKey();
        }
        catch (Exception ex)
        {
            _loading = true;
            ProviderBox.SelectedValue = _editingProvider.ToString();
            _loading = false;
            ShowFeedback(SafeSettingsError(ex));
        }
    }
    private bool _keyDirty;
    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e) { if (!_loading) { _keyDirty = true; RequestAutoSave(); } }
    private void Endpoint_TextChanged(object sender, TextChangedEventArgs e) { if (!_loading) { _endpointDirty = true; RequestAutoSave(); } }
    private void AIEndpoint_PasswordChanged(object sender, RoutedEventArgs e) { if (!_loading) { _aiEndpointDirty = true; RequestAutoSave(); } }
    private void SaveKey()
    {
        if (_aiEndpointDirty)
        {
            var endpoint = AIEndpointBox.Text.Trim();
            if (endpoint.Length != 0) AITranslationProtocol.ParseEndpoint(endpoint);
            _draft.ProtectedAIEndpoint = endpoint.Length == 0 ? "" : SecretProtector.Protect(endpoint);
            _aiEndpointDirty = false;
        }
        if (_endpointDirty)
        {
            var endpoint = EndpointBox.Text.Trim();
            if (endpoint.Length != 0) TranslationService.ParseDeepLXEndpoint(endpoint);
            _draft.ProtectedDeepLXEndpoint = endpoint.Length == 0 ? "" : SecretProtector.Protect(endpoint);
            _endpointDirty = false;
        }
        if (!_keyDirty) return;
        var key = ApiKeyBox.Password.Trim();
        if (key.Any(char.IsControl)) throw new ArgumentException("API Key 不可包含换行或控制字符。");
        if (key.Length == 0) _draft.ProtectedApiKeys.Remove(_editingProvider);
        else _draft.ProtectedApiKeys[_editingProvider] = SecretProtector.Protect(key);
        _keyDirty = false;
    }
    private void LoadKey()
    {
        var wasLoading = _loading;
        _loading = true;
        try { ApiKeyBox.Password = SecretProtector.Unprotect(_draft.ProtectedApiKeys.GetValueOrDefault(_editingProvider, "")); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        { ApiKeyBox.Password = ""; ShowFeedback("当前服务密钥无法解密，请重新输入；未修改前不会覆盖旧密钥。"); }
        try { EndpointBox.Text = SecretProtector.Unprotect(_draft.ProtectedDeepLXEndpoint); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        { EndpointBox.Text = ""; ShowFeedback("DeepLX 地址无法解密，请重新填写；未修改前不会覆盖旧配置。"); }
        try
        {
            var endpoint = SecretProtector.Unprotect(_draft.ProtectedAIEndpoint);
            AIEndpointBox.Text = string.IsNullOrEmpty(endpoint) ? AITranslationProtocol.DefaultEndpoint : endpoint;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        { AIEndpointBox.Text = ""; ShowFeedback("AI 地址无法解密，请重新填写；未修改前不会覆盖旧配置。"); }
        _aiEndpointDirty = false;
        _endpointDirty = false;
        _keyDirty = false;
        _loading = wasLoading;
        AISettingsPanel.Visibility = _draft.Provider == TranslationProvider.AI ? Visibility.Visible : Visibility.Collapsed;
        var isDeepLX = _draft.Provider == TranslationProvider.DeepLX;
        EndpointCard.Visibility = isDeepLX ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyCard.Header = isDeepLX ? "Bearer Token（可选）" : "API Key";
        ApiKeyCard.Description = _draft.Provider == TranslationProvider.AI ? "远程服务必填，本机服务可留空。使用 DPAPI 加密保存。" : isDeepLX ? "地址已含令牌时通常留空；仅实例要求 Bearer 鉴权时填写。" : "Windows DPAPI 加密，仅当前账户可解密。";
        System.Windows.Automation.AutomationProperties.SetName(ApiKeyBox, isDeepLX ? "DeepLX Bearer Token（可选）" : "翻译服务 API Key");
        RegionCard.Visibility = _draft.Provider == TranslationProvider.Microsoft ? Visibility.Visible : Visibility.Collapsed;
        ProviderHint.Text = _draft.Provider switch
        {
            TranslationProvider.AI => "仅支持 Chat Completions 兼容协议，不是 ChatGPT 网页地址。文本将发往所填服务；AI 可能误译，请核对结果。最长等待 90 秒，可按 Esc 取消。关闭 Stream 时检查完整译文后粘贴；开启时收到片段即输入，后续失败不能撤回已输入内容。",
            TranslationProvider.DeepLX => "DeepLX 是第三方服务，不是 DeepL 官方 API。完整 URL 会直接接收待翻译文本，请仅填写可信服务。使用 POST /translate 协议，读取 code / data；旧版服务可能不支持繁体中文。",
            TranslationProvider.Microsoft => "使用 Azure Translator Text API v3 全球端点；不支持专用网络 / 中国区自定义端点。Key 与区域需对应同一个资源。",
            _ => "DeepL API Free 和 Pro 使用不同端点；DeepL 网页版订阅不等同于 API 订阅。简繁中文使用 ZH-HANS / ZH-HANT。"
        };
    }
    private async void TestTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (_testCancellation is not null) return;
        using var cts = new CancellationTokenSource();
        _testCancellation = cts;
        TestButton.IsEnabled = false;
        CancelTestButton.IsEnabled = true;
        TestOutput.Text = "正在连接翻译服务…";
        try
        {
            if (HasValidationErrors(this)) throw new ArgumentException("请先修正数字输入框中的无效值。");
            SaveKey();
            var config = _draft.Clone();
            config.Validate();
            if (config.Provider == TranslationProvider.AI && config.AIStreamEnabled)
            {
                TestOutput.Text = "";
                await foreach (var fragment in _app.Translator.StreamTranslateAsync(TestInput.Text, config, cts.Token))
                    TestOutput.AppendText(fragment);
            }
            else TestOutput.Text = await _app.Translator.TranslateAsync(TestInput.Text, config, cts.Token);
            _app.Log.Write("INFO", "TranslationTest", $"{config.Provider} 连接测试成功。");
            ShowFeedback("测试成功。有效配置会自动保存。");
        }
        catch (OperationCanceledException) { TestOutput.Text = cts.IsCancellationRequested ? "测试已取消。" : "请求超时，请检查网络。"; _app.Log.Write("WARN", "TranslationTest", "测试已取消或超时。"); }
        catch (Exception ex)
        {
            TestOutput.Text = ex is TranslationException or ArgumentException ? ex.Message : ex is HttpRequestException ? "无法连接翻译服务，请检查网络。" : "测试失败，请检查配置。";
            _app.Log.Write("ERROR", "TranslationTest", ex is TranslationException
                ? $"连接测试失败：{ex.Message}"
                : $"连接测试失败（{ex.GetType().Name}），请检查网络或服务配置。");
        }
        finally { _testCancellation = null; TestButton.IsEnabled = true; CancelTestButton.IsEnabled = false; }
    }
    private void CancelTest_Click(object sender, RoutedEventArgs e) => _testCancellation?.Cancel();
    private void ProviderDocs_Click(object sender, RoutedEventArgs e) => OpenPath(_draft.Provider switch
    {
        TranslationProvider.AI => "https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create",
        TranslationProvider.DeepLX => "https://deeplx.owo.network/endpoints/free.html",
        TranslationProvider.Microsoft => "https://learn.microsoft.com/azure/ai-services/translator/text-translation/reference/v3/translate",
        _ => "https://developers.deepl.com/api-reference/translate"
    });
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || e.Key == Key.Tab) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete) { SetHotkey(box, ""); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        uint flags = ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) | ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0)
                     | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0) | ((modifiers & ModifierKeys.Windows) != 0 ? 8u : 0);
        try { SetHotkey(box, Hotkey.Parse(new Hotkey(flags, (uint)KeyInterop.VirtualKeyFromKey(key)).ToString()).ToString()); ShowFeedback("快捷键已填写，将自动保存。"); }
        catch (ArgumentException ex) { ShowFeedback(ex.Message); }
    }
    private static void SetHotkey(TextBox box, string text) { box.SetCurrentValue(TextBox.TextProperty, text); box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); }
    private void ClearHotkey_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string name } && FindName(name) is TextBox box) SetHotkey(box, ""); }
    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    { SetHotkey(CustomKeyBox, "Ctrl+Shift+V"); SetHotkey(TranslateKeyBox, ""); SetHotkey(StreamKeyBox, ""); SetHotkey(PlainKeyBox, ""); SetHotkey(CancelKeyBox, ""); ShowFeedback("已恢复默认快捷键，将自动保存。"); }
    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistory();
    private void RefreshHistory()
    {
        if (_historyView is null) return;
        _historyView.Refresh();
        HistoryEmpty.Visibility = _historyView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = string.IsNullOrEmpty(HistorySearch.Text) ? "还没有粘贴记录\n使用快捷键后，记录会出现在这里。" : "没有匹配的记录";
        HistoryCount.Text = $"共 {_app.History.Entries.Count} 条记录 · {(_app.Settings.HistoryEnabled ? "正在记录" : "记录已关闭")}";
    }
    private async void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryEntry entry }) return;
        try { await ClipboardService.SetTextAsync(entry.Text); ShowFeedback("已复制历史文本，可切换到目标应用粘贴。"); _app.Log.Write("INFO", "HistoryCopy", "已复制一条历史记录。"); }
        catch (Exception) { ShowFeedback("剪贴板暂不可用，请稍后再试。"); }
    }
    private void ViewHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryEntry entry }) return;
        var dialog = new Window
        {
            Owner = this,
            Title = $"{entry.Mode} · {entry.TimeLabel}",
            Width = 650,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = entry.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) }
        };
        WindowHelper.SetUseModernWindowStyle(dialog, true);
        dialog.ShowDialog();
    }
    private void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryEntry entry }) return;
        try { _app.History.Remove(entry); ShowFeedback("已删除记录。"); _app.Log.Write("INFO", "HistoryDelete", "已删除一条历史记录。"); }
        catch (Exception) { ShowFeedback("删除失败，请检查数据目录权限。"); }
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "永久清空所有历史记录？此操作无法撤回。", "清空历史", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _app.History.Clear(); ShowFeedback("历史记录已清空。"); _app.Log.Write("INFO", "HistoryClear", "历史记录已清空。"); }
        catch (Exception) { ShowFeedback("清空失败，请检查数据目录权限。"); }
    }
    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e) => _logView?.Refresh();
    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e) => _logView?.Refresh();
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenPath(_app.Log.DirectoryPath);
    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenPath(_app.Storage.Root);
    private void OpenPath(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception) { ShowFeedback("无法打开链接或目录。"); } }
    private void UpdateRuntime() { RuntimeStatus.Text = _app.Paste.Status; CancelPasteButton.IsEnabled = _app.Paste.IsBusy; SaveButton.IsEnabled = !_app.Paste.IsBusy; if (!_app.Paste.IsBusy) _autoSave?.ResumeIfPending(); }
    private void CancelPaste_Click(object sender, RoutedEventArgs e) => _app.Paste.Cancel();
    private async void Exit_Click(object sender, RoutedEventArgs e) => await _app.ExitAsync();
}
