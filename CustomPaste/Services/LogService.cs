using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using CustomPaste.Models;

namespace CustomPaste.Services;

public sealed class LogService : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string _directory;

    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1024)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly Task _writer;
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public string DirectoryPath => _directory;
    public string? PersistenceError { get; private set; }

    public LogService(string root, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _directory = Path.Combine(root, "Logs");
        Directory.CreateDirectory(_directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    // Messages must be app-authored metadata, never request/response bodies, keys, or exception messages.
    public void Write(string level, string eventName, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, eventName, message);
        if (_dispatcher.CheckAccess()) Add(entry);
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() => Add(entry));
        _queue.Writer.TryWrite(entry);
    }

    private void Add(LogEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > 500) Entries.RemoveAt(Entries.Count - 1);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync())
        {
            try
            {
                var path = Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".1", true);
                await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
                foreach (var old in Directory.EnumerateFiles(_directory, "*.jsonl*"))
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7))
                        File.Delete(old);
                PersistenceError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (PersistenceError is null && !_dispatcher.HasShutdownStarted)
                    _ = _dispatcher.BeginInvoke(() =>
                        Add(new LogEntry(DateTimeOffset.Now, "ERROR", "Logs", "日志文件写入失败，请检查磁盘空间及目录权限。")));
                PersistenceError = "日志文件写入失败";
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }
}