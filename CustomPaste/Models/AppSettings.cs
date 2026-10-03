using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CustomPaste.Services;

namespace CustomPaste.Models;

public enum TranslationProvider
{
    DeepLFree,
    DeepLPro,
    Microsoft,
    DeepLX,
    AI
}

public enum PasteAction
{
    Custom,
    Translate,
    Stream,
    Plain,
    Cancel
}

public sealed class AppSettings : INotifyPropertyChanged
{
    private bool _translateOnPaste;
    public bool TranslateOnPaste { get => _translateOnPaste; set => Set(ref _translateOnPaste, value); }
    private bool _streamOnPaste;
    public bool StreamOnPaste { get => _streamOnPaste; set => Set(ref _streamOnPaste, value); }
    private bool _restoreClipboard = true;
    public bool RestoreClipboard { get => _restoreClipboard; set => Set(ref _restoreClipboard, value); }
    private int _characterDelayMs = 25;
    public int CharacterDelayMs { get => _characterDelayMs; set => Set(ref _characterDelayMs, value); }
    private int _clipboardRestoreDelayMs = 800;
    public int ClipboardRestoreDelayMs { get => _clipboardRestoreDelayMs; set => Set(ref _clipboardRestoreDelayMs, value); }
    private int _maxTextLength = 10000;
    public int MaxTextLength { get => _maxTextLength; set => Set(ref _maxTextLength, value); }
    private bool _historyEnabled = true;
    public bool HistoryEnabled { get => _historyEnabled; set => Set(ref _historyEnabled, value); }
    private int _historyLimit = 100;
    public int HistoryLimit { get => _historyLimit; set => Set(ref _historyLimit, value); }
    private bool _notificationsEnabled = true;
    public bool NotificationsEnabled { get => _notificationsEnabled; set => Set(ref _notificationsEnabled, value); }
    private string _customHotkey = "Ctrl+Shift+V";
    public string CustomHotkey { get => _customHotkey; set => Set(ref _customHotkey, value); }
    private string _translateHotkey = "";
    public string TranslateHotkey { get => _translateHotkey; set => Set(ref _translateHotkey, value); }
    private string _streamHotkey = "";
    public string StreamHotkey { get => _streamHotkey; set => Set(ref _streamHotkey, value); }
    private string _cancelHotkey = "";
    public string CancelHotkey { get => _cancelHotkey; set => Set(ref _cancelHotkey, value); }
    private string _plainHotkey = "";
    public string PlainHotkey { get => _plainHotkey; set => Set(ref _plainHotkey, value); }
    private TranslationProvider _provider = TranslationProvider.DeepLFree;
    public TranslationProvider Provider { get => _provider; set => Set(ref _provider, value); }
    private string _sourceLanguage = "auto";
    public string SourceLanguage { get => _sourceLanguage; set => Set(ref _sourceLanguage, value); }
    private string _targetLanguage = "en";
    public string TargetLanguage { get => _targetLanguage; set => Set(ref _targetLanguage, value); }

    private bool _aIStreamEnabled;
    public bool AIStreamEnabled { get => _aIStreamEnabled; set => Set(ref _aIStreamEnabled, value); }
    private string _protectedAIEndpoint = "";
    public string ProtectedAIEndpoint { get => _protectedAIEndpoint; set => Set(ref _protectedAIEndpoint, value); }
    private string _aIModel = "";
    public string AIModel { get => _aIModel; set => Set(ref _aIModel, value); }
    private string _aIInstructions = "";
    public string AIInstructions { get => _aIInstructions; set => Set(ref _aIInstructions, value); }
    private string _protectedDeepLXEndpoint = "";
    public string ProtectedDeepLXEndpoint { get => _protectedDeepLXEndpoint; set => Set(ref _protectedDeepLXEndpoint, value); }
    private string _microsoftRegion = "";
    public string MicrosoftRegion { get => _microsoftRegion; set => Set(ref _microsoftRegion, value); }

    // Only DPAPI ciphertext is persisted. Plaintext keys never enter settings or logs.
    private Dictionary<TranslationProvider, string> _protectedApiKeys = new();
    public Dictionary<TranslationProvider, string> ProtectedApiKeys { get => _protectedApiKeys; set => Set(ref _protectedApiKeys, value); }
    private string _theme = "System";
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    private string _backdrop = "Mica";
    public string Backdrop { get => _backdrop; set => Set(ref _backdrop, value); }
    private string _accent = "System";
    public string Accent { get => _accent; set => Set(ref _accent, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    public Dictionary<PasteAction, Hotkey> GetHotkeys()
    {
        var result = new Dictionary<PasteAction, Hotkey>();
        foreach (var (action, text) in new[]
                 {
                     (PasteAction.Custom, CustomHotkey), (PasteAction.Translate, TranslateHotkey),
                     (PasteAction.Stream, StreamHotkey), (PasteAction.Plain, PlainHotkey),
                     (PasteAction.Cancel, CancelHotkey)
                 })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var key = Hotkey.Parse(text);
            if (result.ContainsValue(key)) throw new ArgumentException($"快捷键 {key} 被重复使用。请为不同操作设置不同组合。");
            result.Add(action, key);
        }

        return result;
    }

    public void Validate()
    {
        if (CharacterDelayMs is < 1 or > 1000) throw new ArgumentException("输入间隔应为 1–1000 毫秒。");
        if (ClipboardRestoreDelayMs is < 200 or > 5000) throw new ArgumentException("剪贴板恢复延迟应为 200–5000 毫秒。");
        if (HistoryLimit is < 1 or > 1000) throw new ArgumentException("历史记录条数应为 1–1000。");
        if (MaxTextLength is < 1 or > 30000) throw new ArgumentException("单次文本上限应为 1–30000 字符。");
        if (!Enum.IsDefined(Provider)) throw new ArgumentException("不支持的翻译服务。");
        if (!Languages.All.Any(l => l.Code == SourceLanguage) ||
            !Languages.All.Any(l => l.Code == TargetLanguage && l.Code != "auto"))
            throw new ArgumentException("请选择有效的源语言和目标语言。");
        if (Theme is not ("System" or "Light" or "Dark") || Backdrop is not ("None" or "Mica" or "Tabbed") ||
            Accent is not ("System" or "#5267DF" or "#008577" or "#AA477B" or "#C66A21"))
            throw new ArgumentException("外观配置无效。");
        if (ProtectedApiKeys is null) throw new ArgumentException("密钥配置无效。");
        if (MicrosoftRegion is null || MicrosoftRegion.Length > 80 ||
            MicrosoftRegion.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Microsoft 区域仅可包含英文字母、数字和连字符。");
        if (AIModel is null || AIModel.Length > 200 || AIModel.Any(char.IsWhiteSpace))
            throw new ArgumentException("AI 模型 ID 最多 200 字符，不能包含空白。");
        if (AIInstructions is null || AIInstructions.Length > 2000)
            throw new ArgumentException("AI 额外翻译要求最多 2000 字符。");
        GetHotkeys();
    }
}

public sealed record LanguageOption(string Code, string Name);

public static class Languages
{
    public static IReadOnlyList<LanguageOption> All { get; } = new[]
    {
        new LanguageOption("auto", "自动检测"), new("en", "英语"), new("zh-Hans", "简体中文"),
        new("zh-Hant", "繁体中文"), new("ja", "日语"), new("ko", "韩语"), new("de", "德语"),
        new("fr", "法语"), new("es", "西班牙语"), new("pt", "葡萄牙语"), new("it", "意大利语"), new("ru", "俄语")
    };
}