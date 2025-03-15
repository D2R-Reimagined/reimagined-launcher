using D2RReimaginedTools.FileParsers;
using D2RReimaginedTools.Models;

namespace ReimaginedLauncherMaui.Services;

internal class UniqueItemService : IUniqueItemService
{
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "Resources/GameData/data/global/excel/uniqueitems.txt");

    public async Task<IList<UniqueItem>?> GetUniqueItems()
    {
        return await UniqueItemsParser.GetEntries(_filePath);
    }
}