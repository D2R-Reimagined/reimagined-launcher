﻿using System.Text.Json;

namespace ReimaginedLauncher.Generators;

public static class SerializerOptions
{
    public static JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };

    public static JsonSerializerOptions PropertyNameCaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}