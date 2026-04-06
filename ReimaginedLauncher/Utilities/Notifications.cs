using System;
using Avalonia.Notification;
using Avalonia.Threading;

namespace ReimaginedLauncher.Utilities;

public static class Notifications
{
    public static void SendNotification(string message, string badgeType = "Info")
    {
        SessionLogService.AddEntry(message, badgeType);
        Dispatcher.UIThread.Post(() =>
        {
            MainWindow.ManagerInstance
                .CreateMessage()
                .Accent("#1751C3")
                .Animates(true)
                .Background("#333")
                .HasBadge(badgeType)
                .HasMessage(message)
                .Dismiss().WithDelay(TimeSpan.FromSeconds(5))
                .Queue();
        });
    }
}
