using System;
using System.IO;
using System.Threading;

namespace ReimaginedLauncher.Utilities;

public static class LaunchDiagnostics
{
    private static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ReimaginedLauncher");

    private static readonly string LogFilePath = Path.Combine(AppDirectory, "launch.log");

    private static readonly Lock WriteLock = new();

    public static string CurrentLogFilePath => LogFilePath;

    public static void ResetSession() =>
        Append($"{Environment.NewLine}===== Launch Session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");

    public static void Log(string message) =>
        Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");

    // Diagnostics must never fail the operation being logged; another launcher
    // process may be appending to the same file.
    private static void Append(string text)
    {
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(AppDirectory);
                using var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.Write(text);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void LogException(string context, Exception exception)
    {
        SessionLogService.AddEntry($"{context}: {exception.Message}", "Error");
        Log($"{context}: {exception}");
    }
}
