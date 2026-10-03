using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CustomPaste.Models;

namespace CustomPaste.Services;

public static class SecretProtector
{
    public static string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(
        Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string value) => string.IsNullOrEmpty(value)
        ? ""
        : Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}

public sealed class StorageService
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public string Root { get; }
    public string? LoadWarning { get; private set; }

    public StorageService(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomPaste");
        Directory.CreateDirectory(Root);
    }

    public AppSettings LoadSettings()
    {
        var path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new JsonException();
            settings.Validate();
            return settings;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Leave the original recoverable rather than silently discarding a corrupt configuration.
            var backup = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try
            {
                File.Copy(path, backup, false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            LoadWarning = "配置无法读取或校验失败，已使用默认设置；原文件已尽可能备份。";
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.Validate();
        WriteAtomic(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(settings, _json));
    }

    public List<HistoryEntry> LoadHistory()
    {
        var path = Path.Combine(Root, "history.dat");
        if (!File.Exists(path)) return new();
        var data = JsonSerializer.Deserialize<List<HistoryEntry>>(SecretProtector.Unprotect(File.ReadAllText(path)))
                   ?? throw new JsonException("历史记录格式无效。");
        return data.Where(x => x is not null && x.Text is not null && x.Mode is not null).Take(1000).ToList();
    }

    public void SaveHistory(IEnumerable<HistoryEntry> entries) => WriteAtomic(Path.Combine(Root, "history.dat"),
        SecretProtector.Protect(JsonSerializer.Serialize(entries)));

    public void ClearHistory()
    {
        var path = Path.Combine(Root, "history.dat");
        if (File.Exists(path)) File.Delete(path);
    }

    public static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}