namespace CustomPaste.Models;

public sealed record HistoryEntry(Guid Id, DateTimeOffset Timestamp, string Mode, string Text, int SourceLength)
{
    public string TimeLabel => Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string Preview => Text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    public string LengthLabel => $"{Text.Length:N0} 字符";
}

public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Event, string Message)
{
    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}