using System;

namespace ReimaginedLauncher.Utilities;

internal sealed class TweakAssetUnavailableException : Exception
{
    internal TweakAssetUnavailableException(string assetPath, string message)
        : base(message)
    {
        AssetPath = assetPath;
    }

    internal TweakAssetUnavailableException(string assetPath, string message, Exception innerException)
        : base(message, innerException)
    {
        AssetPath = assetPath;
    }

    internal string AssetPath { get; }
}