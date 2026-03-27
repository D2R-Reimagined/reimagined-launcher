using System.Text.Json.Serialization;

namespace ReimaginedLauncher.HttpClients.Models

    public class NexusModsDownloadLinkResponse
    {
        [JsonPropertyName("URI")]
        public string? Uri { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("short_name")]
        public string? ShortName { get; set; }

        [JsonPropertyName("expires")]
        public long Expires { get; set; }
    }