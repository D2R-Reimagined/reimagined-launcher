using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

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

/// <summary>
/// P/Invoke wrapper around the vendored CascLib native binary. The
/// <see cref="ICascNative"/> implementation degrades gracefully when the
/// binary is missing or fails to load, which lets the rest of the launcher
/// build, run, and surface a clear "fastload unavailable" message instead of
/// throwing on startup.
/// </summary>
public sealed class NativeCascLib : ICascNative
{
    /// <summary>
    /// Library import name. The actual file resolved at runtime is
    /// <c>CascLib.dll</c> on Windows and <c>libcasc.so</c> on Linux thanks to
    /// the resolver registered in the static constructor.
    /// </summary>
    private const string LibraryName = "CascLib";

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

        if (!CascOpenStorage(storagePath, localeMask, out IntPtr h) || CascHandles.IsInvalid(h))
        {
            return null;
        }

        return new SafeCascStorageHandle(h);
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

        var data = new CASC_FIND_DATA
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
            Marshal.StructureToPtr(data, buf, fDeleteOld: false);
            IntPtr findHandle = CascFindFirstFile(storage.DangerousGetHandle(), "*", buf, null);
            if (CascHandles.IsInvalid(findHandle)) yield break;
            finder = new SafeCascFindHandle(findHandle);

            do
            {
                data = Marshal.PtrToStructure<CASC_FIND_DATA>(buf);
                string path = NullTerminatedAnsi(data.szFileName);
                if (!string.IsNullOrEmpty(path))
                {
                    yield return new CascFileEntry(
                        Path: path,
                        CKey: (byte[])data.CKey.Clone(),
                        EKey: (byte[])data.EKey.Clone(),
                        FileSize: data.FileSize,
                        LocaleFlags: data.dwLocaleFlags,
                        ContentFlags: data.dwContentFlags,
                        FileDataId: data.dwFileDataId);
                }
            }
            while (CascFindNextFile(finder.DangerousGetHandle(), buf));
        }
        finally
        {
            finder?.Dispose();
            Marshal.FreeHGlobal(buf);
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
            if (!CascOpenFile(storage.DangerousGetHandle(), namePtr, CascLocale.All, CascOpenFlags.OpenByName, out IntPtr h)
                || CascHandles.IsInvalid(h))
            {
                throw new IOException($"CascOpenFile failed for '{cascPath}'.");
            }

            using var file = new SafeCascFileHandle(h);
            buffer ??= new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                if (!CascReadFile(file.DangerousGetHandle(), buffer, (uint)buffer.Length, out uint read))
                {
                    throw new IOException($"CascReadFile failed for '{cascPath}'.");
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

    /// <summary>
    /// Registers a <see cref="NativeLibrary"/> resolver that locates the
    /// vendored binary under <c>runtimes/&lt;rid&gt;/native/</c> next to the
    /// application (the layout produced by the project's <c>.csproj</c>),
    /// then falls back to the application directory and finally the default
    /// system search path.
    /// </summary>
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
                return h;
            }
        }

        // Fall through to default search (system PATH / OS loader).
        if (NativeLibrary.TryLoad(LibraryName, assembly, searchPath, out IntPtr defaultHandle))
        {
            return defaultHandle;
        }

        s_loadFailure = "CascLib native binary could not be located.";
        return IntPtr.Zero;
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

    // -----------------------------------------------------------------------
    // P/Invoke surface. CascLib uses __stdcall (WINAPI) on Windows and the
    // platform default everywhere else; the runtime selects the correct
    // calling convention for [DllImport] on x86 only, so on x64 the choice is
    // ABI-irrelevant. CharSet.Ansi matches the LPCSTR signatures.
    // -----------------------------------------------------------------------

    [DllImport(LibraryName, CharSet = CharSet.Ansi, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CascOpenStorage(
        [MarshalAs(UnmanagedType.LPStr)] string szParams,
        uint dwLocaleMask,
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
}
