# Linux Build & Run

## Prerequisites

- For a release download, no .NET installation is required.
- For development, install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Steam users must enable Steam Play/Proton for Diablo II: Resurrected.
- Battle.net installations require `wine` to be available on `PATH`.

## Release download

Download `D2RReimagined.ReimaginedLauncher.AppImage` from the project's GitHub release, make it executable, and run it:

```bash
chmod +x D2RReimagined.ReimaginedLauncher.AppImage
./D2RReimagined.ReimaginedLauncher.AppImage
```

The launcher detects native and Flatpak Steam installations in their standard locations, including Steam libraries configured in `libraryfolders.vdf`. Custom locations can be selected from the Launch page.

## Steps

```bash
# Restore packages
dotnet restore ReimaginedLauncher.sln

# Build
dotnet build ReimaginedLauncher.sln

# Run the launcher
dotnet run --project ReimaginedLauncher/ReimaginedLauncher.csproj
```

## Publishing

To build a self-contained Linux binary, specify the Linux runtime and Production configuration:

```bash
dotnet publish ReimaginedLauncher/ReimaginedLauncher.csproj -c Production -r linux-x64 --self-contained
```

Output will be in `ReimaginedLauncher/bin/Production/net10.0/linux-x64/publish/`.

## Notes

- Launcher self-updates are supported by the packaged AppImage.
- Steam launches use app ID `2536520` and pass the same Reimagined launch parameters as Windows.
- Battle.net installations are launched through Wine. When the selected game is inside a Wine prefix, the launcher derives and supplies `WINEPREFIX` automatically.
- Save backup discovery includes native Steam, custom Steam libraries, Flatpak Steam, and Wine prefixes. A custom save directory can still be selected in Settings.
