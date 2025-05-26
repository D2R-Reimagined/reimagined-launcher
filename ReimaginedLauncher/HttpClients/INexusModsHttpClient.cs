using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.HttpClients;

public interface INexusModsHttpClient
{
    Task<NexusModsFileListResponse?> GetModFilesAsync(string gameName, int modId);
    Task<NexusModsValidateResponse?> ValidateApiKeyAsync(string? apiKey = "");
    Task<NexusModsFileListResponse?> GenerateDownloadLink(string gameName, int modid, int fileId);

}