using System.Collections.ObjectModel;
using CustomPaste.Models;

namespace CustomPaste.Services;

public sealed class HistoryService
{
    private readonly StorageService _storage;
    public ObservableCollection<HistoryEntry> Entries { get; } = new();
    public HistoryService(StorageService storage) => _storage = storage;

    public void Load(int limit)
    {
        foreach (var entry in _storage.LoadHistory().Take(limit)) Entries.Add(entry);
    }

    public void Add(string mode, string text, int sourceLength, int limit)
    {
        var next = Entries.Prepend(new HistoryEntry(Guid.NewGuid(), DateTimeOffset.Now, mode, text, sourceLength))
            .Take(limit).ToList();
        _storage.SaveHistory(next);
        Replace(next);
    }

    public void Trim(int limit)
    {
        if (Entries.Count <= limit) return;
        var next = Entries.Take(limit).ToList();
        _storage.SaveHistory(next);
        Replace(next);
    }

    public void Remove(HistoryEntry entry)
    {
        _storage.SaveHistory(Entries.Where(x => x.Id != entry.Id));
        Entries.Remove(entry);
    }

    public void Clear()
    {
        _storage.ClearHistory();
        Entries.Clear();
    }

    private void Replace(IEnumerable<HistoryEntry> entries)
    {
        Entries.Clear();
        foreach (var entry in entries) Entries.Add(entry);
    }
}