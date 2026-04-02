using System;
using System.IO;

namespace ReimaginedLauncher.Utilities;

public static class LaunchDiagnostics
{
    private static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ReimaginedLauncher");

    private static readonly string LogFilePath = Path.Combine(AppDirectory, "launch.log");

    public static string CurrentLogFilePath => LogFilePath;

    public static void ResetSession()
    {
        Directory.CreateDirectory(AppDirectory);
        File.AppendAllText(
            LogFilePath,
            $"{Environment.NewLine}===== Launch Session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
    }

    public static void Log(string message)
    {
        Directory.CreateDirectory(AppDirectory);
        File.AppendAllText(
            LogFilePath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
    }

    public static void LogException(string context, Exception exception)
    {
        Log($"{context}: {exception}");
    }
}
