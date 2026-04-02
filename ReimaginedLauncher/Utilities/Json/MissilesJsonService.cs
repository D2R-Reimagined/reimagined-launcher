using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Json;

public static partial class MissilesJsonService
{
    [GeneratedRegex("(\"proc_splash_explode\"\\s*:\\s*)\"[^\"]*\"")]
    private static partial Regex ProcSplashExplodeRegex();

    public static async Task<int> ClearProcSplashExplodeAsync(string missilesFilePath)
    {
        var json = await File.ReadAllTextAsync(missilesFilePath);
        var replacements = 0;
        var updatedJson = ProcSplashExplodeRegex().Replace(json, match =>
        {
            replacements++;
            return $"{match.Groups[1].Value}\"\"";
        });

        if (replacements == 0)
        {
            return 0;
        }

        await File.WriteAllTextAsync(missilesFilePath, updatedJson);
        return replacements;
    }
}
