using System.Threading.Tasks;
using ReimaginedLauncher.HttpClients.Models;
using System.Net;

namespace ReimaginedLauncher.HttpClients;

public interface INexusModsHttpClient
{
    Task<NexusModsFileListResponse?> GetModFilesAsync(string gameName, int modId);
    Task<NexusModsValidateResponse?> ValidateApiKeyAsync(string? apiKey = "");
    Task<(NexusModsDownloadLinkResponse? Link, HttpStatusCode StatusCode)> GenerateDownloadLink(
        string gameName,
        int modid,
        int fileId,
        string? key = null,
        long? expires = null);

}
