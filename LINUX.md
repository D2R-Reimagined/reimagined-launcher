# Linux Build & Run

## Prerequisites
- [.NET SDK](https://dotnet.microsoft.com/download) (project targets `net10.0`)

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

The project currently targets `win-x64` by default. To build a self-contained Linux binary, specify a Linux runtime:

```bash
dotnet publish ReimaginedLauncher/ReimaginedLauncher.csproj -r linux-x64 --self-contained
```

Output will be in `ReimaginedLauncher/bin/Release/net10.0/linux-x64/publish/`.

## Notes

- This is an [Avalonia](https://avaloniaui.net/) desktop app. Linux support depends on Avalonia's compatibility with your desktop environment.
- For development, `dotnet run` is sufficient.
