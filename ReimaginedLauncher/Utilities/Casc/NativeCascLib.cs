using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Safe handle for a CASC storage opened by <c>CascOpenStorage</c>. Released
/// via <c>CascCloseStorage</c>.
/// </summary>
public sealed class SafeCascStorageHandle : SafeHandle
{
    public SafeCascStorageHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    internal SafeCascStorageHandle(IntPtr h) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(h);
    }

    public override bool IsInvalid => CascHandles.IsInvalid(handle);

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeCascLib.CascCloseStorage(handle);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Safe handle for a single CASC file opened by <c>CascOpenFile</c>. Released
/// via <c>CascCloseFile</c>.
/// </summary>
internal sealed class SafeCascFileHandle : SafeHandle
{
    public SafeCascFileHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    internal SafeCascFileHandle(IntPtr h) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(h);
    }

    public override bool IsInvalid => CascHandles.IsInvalid(handle);

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeCascLib.CascCloseFile(handle);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Safe handle for an enumeration cursor returned by <c>CascFindFirstFile</c>.
/// Released via <c>CascFindClose</c>.
/// </summary>
internal sealed class SafeCascFindHandle : SafeHandle
{
    public SafeCascFindHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    internal SafeCascFindHandle(IntPtr h) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(h);
    }

    public override bool IsInvalid => CascHandles.IsInvalid(handle);

    protected override bool ReleaseHandle()
    {
        try
        {
            return NativeCascLib.CascFindClose(handle);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>P/Invoke wrapper around vendored CascLib; degrades gracefully when the binary is missing so the launcher can surface "fastload unavailable" instead of crashing.</summary>
public sealed class NativeCascLib : ICascNative
{
    /// <summary>Import name; resolved to <c>CascLib.dll</c> (Windows) or <c>libcasc.so</c> (Linux) by the registered resolver.</summary>
    private const string LibraryName = "CascLib";

    // CDN region used when CascLib falls back to online for EKeys not indexed locally.
    // "us" pairs with the enUS locale and is the default for D2R installs we care about.
    private const string DefaultRegion = "us";

    private static readonly object ResolverLock = new();
    private static bool s_resolverRegistered;
    private static string? s_loadFailure;

    private readonly bool _available;
    private readonly string? _unavailableReason;

    public NativeCascLib()
    {
        EnsureResolver();
        // Probe the library by calling a cheap export. We use
        // CascCdnGetDefault which has no preconditions.
        try
        {
            _ = CascCdnGetDefault();
            _available = true;
        }
        catch (DllNotFoundException ex)
        {
            _available = false;
            _unavailableReason = $"Native CascLib not found: {ex.Message}";
        }
        catch (Exception ex) when (ex is BadImageFormatException or EntryPointNotFoundException)
        {
            _available = false;
            _unavailableReason = $"Native CascLib could not be loaded: {ex.Message}";
        }

        if (!_available && s_loadFailure is not null)
        {
            _unavailableReason = s_loadFailure;
        }
    }

    public bool IsAvailable => _available;
    public string? UnavailableReason => _unavailableReason;

    public SafeCascStorageHandle? OpenStorage(string storagePath, uint localeMask = CascLocale.All)
    {
        if (!_available) return null;
        if (string.IsNullOrWhiteSpace(storagePath)) return null;

        IntPtr szLocalPath = IntPtr.Zero;
        IntPtr szRegion = IntPtr.Zero;
        // Keep the delegate rooted so the GC can't collect it while CascLib is calling back.
        PfnProgressCallback progressDelegate = OnCascProgress;
        var keepAlive = GCHandle.Alloc(progressDelegate);

        try
        {
            szLocalPath = Marshal.StringToCoTaskMemAnsi(storagePath);
            // Default region "us" (enUS): needed by CascLib to select a CDN host when
            // online fallback runs for EKeys not present in the local .idx map (Steam D2R).
            szRegion = Marshal.StringToCoTaskMemAnsi(DefaultRegion);

            // For the local-path branch (Steam, BNet — anywhere `.build.info` exists), CascLib
            // sources `CASC_FEATURE_ALLOW_DOWNLOAD` *only* from `pArgs->dwFlags`; the
            // `bOnlineStorage` parameter is consulted only when no local build file is found.
            // Without this bit, `LoadEncodingManifest` skips its CDN fallback and returns
            // ERROR_FILE_NOT_FOUND for any EKey not covered by the local .idx map (Steam D2R).
            const uint dwFlags = CascFeature.AllowDownload;

            var args = new CASC_OPEN_STORAGE_ARGS
            {
                Size = (UIntPtr)Marshal.SizeOf<CASC_OPEN_STORAGE_ARGS>(),
                szLocalPath = szLocalPath,
                szRegion = szRegion,
                PfnProgressCallback = Marshal.GetFunctionPointerForDelegate(progressDelegate),
                dwLocaleMask = localeMask,
                dwFlags = dwFlags,
            };

            if (!CascOpenStorageEx(IntPtr.Zero, ref args, true, out IntPtr h) || CascHandles.IsInvalid(h))
            {
                // Surface CascLib's own error code so callers can tell ERROR_FILE_NOT_FOUND
                // (bad path / DLL not picked up) from ERROR_BAD_FORMAT (parse failure) etc.
                uint err = TryGetCascError();
                LaunchDiagnostics.Log(
                    $"CascOpenStorageEx returned false for '{storagePath}' (localeMask=0x{localeMask:X}, region='{DefaultRegion}', dwFlags=0x{dwFlags:X}, online=true); CascLib error {err} (0x{err:X}).");
                return null;
            }

            return new SafeCascStorageHandle(h);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Surface native faults as IOException so the caller can record/notify
            // instead of crashing the launcher. bool=false outcomes still return null.
            uint err = TryGetCascError();
            throw new IOException($"CascOpenStorageEx threw for '{storagePath}' (CascLib error {err} / 0x{err:X}): {ex.Message}", ex);
        }
        finally
        {
            if (szLocalPath != IntPtr.Zero) Marshal.FreeCoTaskMem(szLocalPath);
            if (szRegion != IntPtr.Zero) Marshal.FreeCoTaskMem(szRegion);
            if (keepAlive.IsAllocated) keepAlive.Free();
        }
    }

    /// <summary>
    /// Matches CascLib's <c>PFNPROGRESSCALLBACK</c>. Return <c>true</c> to cancel; we never cancel from here.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate bool PfnProgressCallback(
        IntPtr ptrUserParam,
        CASC_PROGRESS_MSG progressMsg,
        [MarshalAs(UnmanagedType.LPStr)] string? szObject,
        uint currentValue,
        uint totalValue);

    private static bool OnCascProgress(
        IntPtr ptrUserParam,
        CASC_PROGRESS_MSG progressMsg,
        string? szObject,
        uint currentValue,
        uint totalValue)
    {
        // Last breadcrumb before a CascOpenStorageEx failure tells us which step
        // (loading indexes, encoding manifest, install/download, etc.) errored.
        try
        {
            LaunchDiagnostics.Log(
                $"CASC progress: {progressMsg} '{szObject ?? ""}' ({currentValue}/{totalValue}).");
        }
        catch
        {
            // Never let a logging fault propagate into the native callback.
        }
        return false;
    }

    public CascStorageProduct? GetStorageProduct(SafeCascStorageHandle storage)
    {
        if (!_available || storage is null || storage.IsInvalid) return null;

        var product = new CASC_STORAGE_PRODUCT { szCodeName = new byte[0x1C] };
        int size = Marshal.SizeOf<CASC_STORAGE_PRODUCT>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(product, buf, fDeleteOld: false);
            if (!CascGetStorageInfo(storage.DangerousGetHandle(),
                    CASC_STORAGE_INFO_CLASS.CascStorageProduct, buf, (UIntPtr)size, out _))
            {
                return null;
            }
            product = Marshal.PtrToStructure<CASC_STORAGE_PRODUCT>(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        string name = NullTerminatedAnsi(product.szCodeName);
        return new CascStorageProduct(name, product.BuildNumber);
    }

    public IEnumerable<CascFileEntry> EnumerateFiles(SafeCascStorageHandle storage)
    {
        if (!_available || storage is null || storage.IsInvalid) yield break;

        var seed = new CASC_FIND_DATA
        {
            szFileName = new byte[CASC_FIND_DATA.MaxPath],
            CKey = new byte[CASC_FIND_DATA.Md5HashSize],
            EKey = new byte[CASC_FIND_DATA.Md5HashSize]
        };

        int structSize = Marshal.SizeOf<CASC_FIND_DATA>();
        IntPtr buf = Marshal.AllocHGlobal(structSize);
        SafeCascFindHandle? finder = null;
        try
        {
            Marshal.StructureToPtr(seed, buf, fDeleteOld: false);
            IntPtr findHandle;
            try
            {
                findHandle = CascFindFirstFile(storage.DangerousGetHandle(), "*", buf, null);
            }
            catch (Exception ex)
            {
                throw new IOException("CascFindFirstFile threw: " + ex.Message, ex);
            }

            if (CascHandles.IsInvalid(findHandle)) yield break;
            finder = new SafeCascFindHandle(findHandle);

            while (true)
            {
                CascFileEntry? next = TryReadFindEntry(buf);
                if (next is not null)
                {
                    yield return next;
                }

                bool more;
                try
                {
                    more = CascFindNextFile(finder.DangerousGetHandle(), buf);
                }
                catch
                {
                    // Abort the walk gracefully; the next index pass will retry.
                    yield break;
                }

                if (!more) yield break;
            }
        }
        finally
        {
            finder?.Dispose();
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Marshals a <c>CASC_FIND_DATA</c> from the native buffer, swallowing
    /// per-row marshalling exceptions so a single malformed entry doesn't
    /// abort the entire enumeration.
    /// </summary>
    private static CascFileEntry? TryReadFindEntry(IntPtr buf)
    {
        try
        {
            var data = Marshal.PtrToStructure<CASC_FIND_DATA>(buf);
            string fullName = NullTerminatedAnsi(data.szFileName);
            if (string.IsNullOrEmpty(fullName)) return null;

            // Canonicalise by stripping the TVFS namespace prefix; FullName retains the
            // prefixed name because CascOpenFile (OpenByName) requires the exact original.
            string path = CascExtractionFilter.StripCascNamespace(fullName);
            if (string.IsNullOrEmpty(path)) return null;

            return new CascFileEntry(
                Path: path,
                CKey: (byte[])data.CKey.Clone(),
                EKey: (byte[])data.EKey.Clone(),
                FileSize: data.FileSize,
                LocaleFlags: data.dwLocaleFlags,
                ContentFlags: data.dwContentFlags,
                FileDataId: data.dwFileDataId,
                FullName: fullName);
        }
        catch
        {
            return null;
        }
    }

    public long ExtractTo(SafeCascStorageHandle storage, string cascPath, Stream destination, byte[]? buffer = null)
    {
        if (!_available) throw new InvalidOperationException(_unavailableReason ?? "CascLib unavailable.");
        if (storage is null || storage.IsInvalid) throw new ArgumentException("Storage handle is invalid.", nameof(storage));
        if (string.IsNullOrWhiteSpace(cascPath)) throw new ArgumentException("Path is required.", nameof(cascPath));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        IntPtr namePtr = Marshal.StringToCoTaskMemAnsi(cascPath);
        try
        {
            bool opened;
            IntPtr h;
            try
            {
                opened = CascOpenFile(storage.DangerousGetHandle(), namePtr, CascLocale.All, CascOpenFlags.OpenByName, out h);
            }
            catch (Exception ex)
            {
                throw new IOException($"CascOpenFile threw for '{cascPath}': {ex.Message}", ex);
            }

            if (!opened || CascHandles.IsInvalid(h))
            {
                throw new IOException($"CascOpenFile failed for '{cascPath}'.");
            }

            using var file = new SafeCascFileHandle(h);
            buffer ??= new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                bool readOk;
                uint read;
                try
                {
                    readOk = CascReadFile(file.DangerousGetHandle(), buffer, (uint)buffer.Length, out read);
                }
                catch (Exception ex)
                {
                    throw new IOException($"CascReadFile threw for '{cascPath}' after {total} bytes: {ex.Message}", ex);
                }

                if (!readOk)
                {
                    throw new IOException($"CascReadFile failed for '{cascPath}' after {total} bytes.");
                }
                if (read == 0) break;
                destination.Write(buffer, 0, (int)read);
                total += read;
            }

            return total;
        }
        finally
        {
            if (namePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namePtr);
        }
    }

    private static string NullTerminatedAnsi(byte[] buffer)
    {
        if (buffer is null || buffer.Length == 0) return string.Empty;
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.GetEncoding(0).GetString(buffer, 0, end);
    }

    /// <summary>Registers a resolver that probes <c>runtimes/&lt;rid&gt;/native/</c>, the app dir, then the system search path.</summary>
    private static void EnsureResolver()
    {
        if (s_resolverRegistered) return;
        lock (ResolverLock)
        {
            if (s_resolverRegistered) return;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeCascLib).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Resolver already set for this assembly; treat as registered.
            }
            s_resolverRegistered = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in EnumerateCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr h))
            {
                LogLoadedNative(candidate);
                return h;
            }
        }

        // Fall through to default search (system PATH / OS loader).
        if (NativeLibrary.TryLoad(LibraryName, assembly, searchPath, out IntPtr defaultHandle))
        {
            LogLoadedNative("<default search: " + LibraryName + ">");
            return defaultHandle;
        }

        s_loadFailure = "CascLib native binary could not be located.";
        return IntPtr.Zero;
    }

    // Resolver is invoked per-import, not per-process; dedupe to keep launch.log readable.
    private static int s_loadLogged;

    private static void LogLoadedNative(string candidate)
    {
        if (System.Threading.Interlocked.Exchange(ref s_loadLogged, 1) != 0)
        {
            return;
        }
        try
        {
            string detail;
            if (File.Exists(candidate))
            {
                var mtime = File.GetLastWriteTimeUtc(candidate);
                long size = new FileInfo(candidate).Length;
                detail = $"mtimeUtc={mtime:o}, size={size}";
            }
            else
            {
                detail = "mtime=<unavailable>";
            }
            LaunchDiagnostics.Log($"CascLib native loaded: '{candidate}' ({detail}).");
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.Log($"CascLib native loaded: '{candidate}' (mtime probe failed: {ex.Message}).");
        }
    }

    private static uint TryGetCascError()
    {
        try
        {
            return GetCascError();
        }
        catch (EntryPointNotFoundException)
        {
            // Older CascLib builds without GetCascError export.
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        string baseDir = AppContext.BaseDirectory;
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 when OperatingSystem.IsWindows() => "win-x64",
            Architecture.X64 when OperatingSystem.IsLinux() => "linux-x64",
            _ => string.Empty
        };

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(baseDir, "CascLib.dll");
            if (!string.IsNullOrEmpty(rid))
                yield return Path.Combine(baseDir, "runtimes", rid, "native", "CascLib.dll");
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(baseDir, "libcasc.so");
            if (!string.IsNullOrEmpty(rid))
                yield return Path.Combine(baseDir, "runtimes", rid, "native", "libcasc.so");
        }
    }

    // P/Invoke surface. CharSet.Ansi matches the LPCSTR signatures.

    // CascOpenStorageEx: structured open API. szParams is ignored when args.szLocalPath is set.
    // bOnlineStorage=false because this launcher only opens local D2R installs.
    [DllImport(LibraryName, CharSet = CharSet.Ansi, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascOpenStorageEx(
        IntPtr szParams,
        ref CASC_OPEN_STORAGE_ARGS pArgs,
        [MarshalAs(UnmanagedType.U1)] bool bOnlineStorage,
        out IntPtr phStorage);

    [DllImport(LibraryName, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascCloseStorage(IntPtr hStorage);

    [DllImport(LibraryName, CharSet = CharSet.Ansi, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascGetStorageInfo(
        IntPtr hStorage,
        CASC_STORAGE_INFO_CLASS InfoClass,
        IntPtr pvStorageInfo,
        UIntPtr cbStorageInfo,
        out UIntPtr pcbLengthNeeded);

    [DllImport(LibraryName, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascOpenFile(
        IntPtr hStorage,
        IntPtr pvFileName,
        uint dwLocaleFlags,
        uint dwOpenFlags,
        out IntPtr PtrFileHandle);

    [DllImport(LibraryName, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascGetFileSize64(IntPtr hFile, out ulong PtrFileSize);

    [DllImport(LibraryName, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascReadFile(
        IntPtr hFile,
        [Out] byte[] lpBuffer,
        uint dwToRead,
        out uint pdwRead);

    [DllImport(LibraryName, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascCloseFile(IntPtr hFile);

    [DllImport(LibraryName, CharSet = CharSet.Ansi, SetLastError = false)]
    internal static extern IntPtr CascFindFirstFile(
        IntPtr hStorage,
        [MarshalAs(UnmanagedType.LPStr)] string szMask,
        IntPtr pFindData,
        [MarshalAs(UnmanagedType.LPStr)] string? szListFile);

    [DllImport(LibraryName, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascFindNextFile(IntPtr hFind, IntPtr pFindData);

    [DllImport(LibraryName, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascFindClose(IntPtr hFind);

    [DllImport(LibraryName, SetLastError = false)]
    internal static extern IntPtr CascCdnGetDefault();

    [DllImport(LibraryName, SetLastError = false)]
    internal static extern uint GetCascError();
}
