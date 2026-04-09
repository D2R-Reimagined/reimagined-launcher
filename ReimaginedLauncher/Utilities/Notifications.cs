using System;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;

namespace ReimaginedLauncher.Utilities;

public static class Notifications
{
    public static void SendNotification(string message, string badgeType = "Info")
    {
        SessionLogService.AddEntry(message, badgeType);
        Dispatcher.UIThread.Post(() =>
        {
            MainWindow.NotificationManager?.Show(new Notification(
                badgeType,
                message,
                ParseNotificationType(badgeType),
                TimeSpan.FromSeconds(5)));
        });
    }

    private static NotificationType ParseNotificationType(string badgeType)
    {
        return badgeType switch
        {
            "Success" => NotificationType.Success,
            "Warning" => NotificationType.Warning,
            "Error" => NotificationType.Error,
            _ => NotificationType.Information
        };
    }
}
