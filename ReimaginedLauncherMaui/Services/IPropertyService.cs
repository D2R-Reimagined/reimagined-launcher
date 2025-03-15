using D2RReimaginedTools.Models;

namespace ReimaginedLauncherMaui.Services;

public interface IPropertyService
{
    Task<IList<Property>> GetProperties();
    Task<IDictionary<string, string>> GetPropertyDescriptions();
}