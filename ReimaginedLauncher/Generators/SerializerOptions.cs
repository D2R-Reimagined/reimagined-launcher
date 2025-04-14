using System.Text.Json;

namespace ReimaginedLauncher.Generators;

public static class SerializerOptions
{
    public static JsonSerializerOptions Indented = new JsonSerializerOptions
    {
        WriteIndented = true
    };
}