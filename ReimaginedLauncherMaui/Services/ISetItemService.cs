using ReimaginedLauncherMaui.Model;

namespace ReimaginedLauncherMaui.Services;

public interface ISetItemService
{
    Task<IList<SetItem>> GetItems();
}