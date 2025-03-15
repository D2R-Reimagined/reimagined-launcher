using D2RReimaginedTools.Extensions;
using D2RReimaginedTools.Models;

namespace ReimaginedLauncherMaui.Services;

public class PropertyService : IPropertyService
{
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "Resources/GameData/data/global/excel/properties.txt");

    public async Task<IList<Property>> GetProperties()
    {
        var lines = (await File.ReadAllLinesAsync(_filePath)).Skip(1); // Skip header line

        return lines.Select(line => line.Split('\t'))
            .Select(columns => new Property
            {
                Code = columns[0],
                Enabled = columns[1].ToBool(),
                PropertyFunctions = new List<PropertyFunction>()
                {
                    new() { Func = columns[2].ToInt(), Stat = columns[3], Set = columns[4].ToInt(), Val = columns[5].ToInt() },
                    new() { Func = columns[6].ToInt(), Stat = columns[7], Set = columns[8].ToInt(), Val = columns[9].ToInt() },
                    new() { Func = columns[10].ToInt(), Stat = columns[11], Set = columns[12].ToInt(), Val = columns[13].ToInt() },
                    new() { Func = columns[14].ToInt(), Stat = columns[15], Set = columns[16].ToInt(), Val = columns[17].ToInt() },
                    new() { Func = columns[18].ToInt(), Stat = columns[19], Set = columns[20].ToInt(), Val = columns[21].ToInt() },
                    new() { Func = columns[22].ToInt(), Stat = columns[23], Set = columns[24].ToInt(), Val = columns[25].ToInt() },
                    new() { Func = columns[26].ToInt(), Stat = columns[27], Set = columns[28].ToInt(), Val = columns[29].ToInt() },
                },
                Tooltip = columns[30],
                Parameter = columns[31],
                Min = columns[32],
                Max = columns[33],
                Notes = columns[34],
                Eol = columns[35].ToInt()
            })
            .ToList();
    }

    public async Task<IDictionary<string, string>> GetPropertyDescriptions()
    {
        var properties = await GetProperties();
        return properties.ToDictionary(p => p.Code, p => p.Tooltip);
    }
}