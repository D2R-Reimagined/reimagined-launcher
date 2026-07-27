# Native runtimes

This folder vendors the native CascLib binaries used by `Utilities/Casc/NativeCascLib.cs`
(Phase 1 CASC fastload). Layout follows the standard .NET `runtimes/<rid>/native/`
convention so a future `NativeLibrary.SetDllImportResolver` can locate the right
binary per RID without any custom MSBuild logic.

## Contents

- `win-x64/native/CascLib.dll` — Windows x64 build of CascLib.
- `linux-x64/native/libcasc.so` — Linux x64 build of CascLib (best-effort tier-2,
  used when the launcher runs natively on SteamOS / desktop Linux; under Proton
  the Windows DLL is used transparently).
- `CascLib.LICENSE` — upstream MIT license / copyright notice (attribution).

## Upstream

CascLib by Ladislav Zezula — https://github.com/ladislav-zezula/CascLib  
Licensed under the MIT License (see `CascLib.LICENSE`). Redistribution of the
compiled binaries in this repository is permitted under that license provided
the copyright notice is retained, which the `CascLib.LICENSE` file satisfies.

## How these binaries were produced

Both binaries were compiled locally from a clean checkout of the upstream
CascLib repository:

- Windows: `make-msvc.bat` (or the `CascLib_dll` MSBuild target in
  `CascLib_dll.vcxproj`, x64 / Release configuration). Output:
  `bin/CascLib_dll/x64/Release/CascLib.dll`.
- Linux: `cmake -S . -B build && cmake --build build --config Release`. Output:
  `libcasc.so`.

## Updating the binaries

1. Pull the latest upstream CascLib (or check out the tag you want to ship).
2. Rebuild for each RID using the steps above.
3. Replace the corresponding file in `runtimes/<rid>/native/`.
4. If the upstream `LICENSE` text changed, refresh `runtimes/CascLib.LICENSE`.
5. Rebuild this project — `CascLib.dll` is copied to the output root, so the
   exe will pick it up automatically on next launch.

## How they get to the runtime

Wired in `ReimaginedLauncher.csproj`:

- `CascLib.dll` is copied to the output root next to `ReimaginedLauncher.exe`,
  which is where the default P/Invoke loader looks first on Windows.
- `libcasc.so` is copied to `runtimes/linux-x64/native/` in the output tree;
  the custom `NativeLibrary.SetDllImportResolver` in `NativeCascLib.cs` will
  load it from there on Linux.
- For Production / self-contained publishes, `IncludeNativeLibrariesForSelfExtract`
  is already `true`, so the bundled single-file exe extracts the native library
  on first run.

If a binary is missing the project still builds; the launcher will degrade
gracefully and the CASC fastload UI will surface "native library missing" rather
than crash.
