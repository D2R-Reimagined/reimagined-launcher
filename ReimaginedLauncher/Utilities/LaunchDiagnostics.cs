using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

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
        SessionLogService.AddEntry($"{context}: {exception.Message}", "Error");

        var builder = new StringBuilder();
        builder.Append(context).Append(": ").AppendLine(exception.ToString());

        var hint = ExplainException(context, exception);
        if (!string.IsNullOrEmpty(hint))
        {
            builder.Append("    Hint: ").AppendLine(hint);
        }

        Log(builder.ToString().TrimEnd());
    }

    // Maps recognizable plugin/CASC failure messages to a short human-readable
    // explanation so launch.log readers (and bug reporters) understand the cause
    // without spelunking the source. Returns null when no specific hint applies.
    private static string? ExplainException(string context, Exception exception)
    {
        var message = exception.Message ?? string.Empty;

        // "swapRowIdentifier 'X' matches N rows in <file>.txt. Use a numeric 0-based index to disambiguate."
        // Same pattern fires for rowIdentifier on swapRow/replaceRow/cloneRow when the named row appears more than once.
        var multiMatch = Regex.Match(
            message,
            @"(?<field>rowIdentifier|swapRowIdentifier)\s+'(?<value>[^']*)'\s+matches\s+(?<count>\d+)\s+rows\s+in\s+(?<file>[^\s]+)",
            RegexOptions.IgnoreCase);
        if (multiMatch.Success)
        {
            return $"The plugin tried to target a row by name ('{multiMatch.Groups["value"].Value}') in {multiMatch.Groups["file"].Value}, " +
                   $"but {multiMatch.Groups["count"].Value} rows in that file share that name. " +
                   "Edit the plugin JSON and replace the name with a numeric 0-based row index (e.g. \"rowIdentifier\": 42), " +
                   "or contact the plugin author to ship a disambiguated identifier.";
        }

        if (Regex.IsMatch(message, @"swapRow.*cannot swap a row with itself", RegexOptions.IgnoreCase))
        {
            return "swapRow received two identifiers that resolve to the same row. " +
                   "Either pick a different swapRowIdentifier or use replaceRow/modify if a swap was not intended.";
        }

        if (Regex.IsMatch(message, @"swapRow.*must specify 'swapRowIdentifier'", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(message, @"swapRow.*must specify 'rowIdentifier'", RegexOptions.IgnoreCase))
        {
            return "swapRow exchanges exactly two rows and requires both 'rowIdentifier' and 'swapRowIdentifier' " +
                   "(each a numeric 0-based row index or a name that matches a single row).";
        }

        if (Regex.IsMatch(message, @"cloneRow .*does not support insertion at a specific index", RegexOptions.IgnoreCase))
        {
            return "cloneRow with mode='add' always appends. Remove 'rowIdentifier' to append, " +
                   "or switch to mode='replace' if the intent is to overwrite an existing row.";
        }

        if (Regex.IsMatch(message, @"addRow operation .* must specify at least one column", RegexOptions.IgnoreCase))
        {
            return "addRow inserts a brand-new row, so the plugin must declare which columns to populate via " +
                   "either a 'columns' array or a single 'column'/'updatedValue' pair.";
        }

        if (Regex.IsMatch(message, @"does not support the array form of 'rowIdentifier'", RegexOptions.IgnoreCase))
        {
            return "This operation can only target a single row. Use a string name or a numeric 0-based index for 'rowIdentifier' " +
                   "instead of the array (multi-match) form.";
        }

        // Asset copy / mod root protection failures.
        if (Regex.IsMatch(message, @"asset .*outside the mod (root|directory)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(message, @"target path .*escapes", RegexOptions.IgnoreCase))
        {
            return "The plugin asked to write a file outside of the mod folder. " +
                   "This is blocked for safety; verify the asset 'target' path uses forward slashes and stays inside the mod.";
        }

        // File system / permission issues that commonly surface on Windows.
        if (exception is UnauthorizedAccessException)
        {
            return "Windows denied access to a file. Ensure the launcher is not running while the game is open, " +
                   "the mod folder is not read-only, and that no antivirus is locking files in the install directory.";
        }

        if (exception is IOException && Regex.IsMatch(message, @"being used by another process", RegexOptions.IgnoreCase))
        {
            return "A file is locked by another process. Close Diablo II: Resurrected and any text editors viewing the mod files, then retry.";
        }

        // Plugin manifest / state load failures bubble up with this context.
        if (context.IndexOf("Failed to apply plugin", StringComparison.OrdinalIgnoreCase) >= 0 &&
            string.IsNullOrEmpty(message) == false &&
            !message.StartsWith("Hint:", StringComparison.OrdinalIgnoreCase))
        {
            return "Plugin application aborted before completion. The mod files for this plugin were not modified, " +
                   "but other plugins applied earlier in the same launch may have already written changes.";
        }

        return null;
    }
}
