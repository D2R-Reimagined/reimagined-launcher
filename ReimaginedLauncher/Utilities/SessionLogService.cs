using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace ReimaginedLauncher.Utilities;

public record SessionLogEntry(string Message, string Type, DateTime Timestamp);

public static class SessionLogService
{
    public static ObservableCollection<SessionLogEntry> Entries { get; } = new();

    public static void AddEntry(string message, string type)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Entries.Count >= 200)
            {
                Entries.RemoveAt(0);
            }
            Entries.Add(new SessionLogEntry(message, type, DateTime.Now));
        });
    }
}
