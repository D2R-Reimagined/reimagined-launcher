using System.Threading.Tasks;
using D2RReimaginedTools.JsonFileParsers;

namespace ReimaginedLauncher.Utilities.Json;

public static class MissilesJsonService
{
    private const string ProcSplashExplodeKey = "proc_splash_explode";

    public static async Task<int> ClearProcSplashExplodeAsync(string missilesFilePath)
    {
        var parser = new MissilesFileParser(missilesFilePath);
        var existing = await parser.GetMissileValueByKeyAsync(ProcSplashExplodeKey);

        if (existing is null)
        {
            return 0;
        }

        if (existing.Length == 0)
        {
            // Already cleared; nothing to rewrite, but the entry exists so report success.
            return 1;
        }

        var replaced = await parser.ReplaceMissileValueAsync(ProcSplashExplodeKey, string.Empty);
        return replaced ? 1 : 0;
    }
}
