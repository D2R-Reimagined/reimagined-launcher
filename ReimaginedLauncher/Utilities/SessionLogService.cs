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
        var entry = new SessionLogEntry(message, type, DateTime.Now);
        try
        {
            if (Avalonia.Application.Current is null)
            {
                if (Entries.Count >= 200)
                {
                    Entries.RemoveAt(0);
                }
                Entries.Add(entry);
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (Entries.Count >= 200)
                {
                    Entries.RemoveAt(0);
                }
                Entries.Add(entry);
            });
        }
        catch (InvalidOperationException)
        {
            // UI dispatcher is not initialized yet during pre-UI startup checks.
        }
    }
}
