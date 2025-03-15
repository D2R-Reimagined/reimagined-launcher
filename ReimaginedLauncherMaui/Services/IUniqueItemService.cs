using D2RReimaginedTools.Models;

namespace ReimaginedLauncherMaui.Services;

public interface IUniqueItemService
{
    Task<IList<UniqueItem>> GetUniqueItems();
}