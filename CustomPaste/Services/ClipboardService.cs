using System.Runtime.InteropServices;
using System.Windows;

namespace CustomPaste.Services;

public static class ClipboardService
{
    public static async Task<T> RetryAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        for (var i = 0; ; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return action();
            }
            catch (COMException) when (i < 5)
            {
                await Task.Delay(40 * (i + 1), cancellationToken);
            }
        }
    }

    public static IDataObject? CaptureSnapshot()
    {
        var source = Clipboard.GetDataObject();
        if (source is null) return null;
        var snapshot = new DataObject();
        foreach (var format in source.GetFormats(false))
        {
            var value = source.GetData(format, false);
            if (value is MemoryStream memory) value = new MemoryStream(memory.ToArray());
            if (value is System.Windows.Media.Imaging.BitmapSource bitmap)
            {
                var clone = bitmap.Clone();
                if (clone.CanFreeze) clone.Freeze();
                value = clone;
            }

            if (value is not null) snapshot.SetData(format, value, false);
        }

        return snapshot;
    }

    public static Task SetTextAsync(string text) => RetryAsync(() =>
    {
        Clipboard.SetText(text);
        return true;
    });
}