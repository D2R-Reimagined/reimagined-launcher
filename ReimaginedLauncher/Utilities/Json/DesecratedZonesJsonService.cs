using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities.Json;

public static partial class DesecratedZonesJsonService
{
    [GeneratedRegex(
        @"\}\s*\]\s*\},\s*\{\s*""type""\s*:\s*""DesecratedZone""\s*,\s*""name""\s*:\s*""desecratedzones_desecrated_zones_0_zones_\d+""\s*,\s*""id""\s*:\s*""Act[2-5]-Auto""\s*,\s*""levels""\s*:\s*\[",
        RegexOptions.Singleline)]
    private static partial Regex ActAutoZoneBoundaryRegex();

    public static async Task<int> MergeActAutoZonesAsync(string desecratedZonesFilePath)
    {
        var json = await File.ReadAllTextAsync(desecratedZonesFilePath);
        var replacements = 0;
        var updatedJson = ActAutoZoneBoundaryRegex().Replace(json, _ =>
        {
            replacements++;
            return "},";
        });

        if (replacements == 0)
        {
            return 0;
        }

        await File.WriteAllTextAsync(desecratedZonesFilePath, updatedJson);
        return replacements;
    }
}
