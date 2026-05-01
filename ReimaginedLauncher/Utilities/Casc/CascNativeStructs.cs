using System;
using System.Runtime.InteropServices;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Mirrors <c>CASC_FIND_DATA</c> from CascLib's <c>CascLib.h</c> as built for
/// Windows x64. Field order, sizes, and packing must match the native struct
/// exactly. Updates to the vendored CascLib must be reviewed against this
/// definition.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
internal struct CASC_FIND_DATA
{
    /// <summary>Win32 <c>MAX_PATH</c> as used by the Windows build of CascLib.</summary>
    public const int MaxPath = 260;

    /// <summary>Length of an MD5 hash in bytes (CKey/EKey).</summary>
    public const int Md5HashSize = 16;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxPath)]
    public byte[] szFileName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Md5HashSize)]
    public byte[] CKey;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Md5HashSize)]
    public byte[] EKey;

    public ulong TagBitMask;
    public ulong FileSize;

    /// <summary>Pointer into <see cref="szFileName"/>; do not free.</summary>
    public IntPtr szPlainName;

    public uint dwFileDataId;
    public uint dwLocaleFlags;
    public uint dwContentFlags;
    public uint dwSpanCount;

    /// <summary>
    /// Native struct uses a single bit-field <c>DWORD bFileAvailable:1</c>; we
    /// model it as a full DWORD because the compiler still allocates 4 bytes.
    /// </summary>
    public uint Flags;
}

/// <summary>Mirrors <c>CASC_STORAGE_INFO_CLASS</c>.</summary>
internal enum CASC_STORAGE_INFO_CLASS
{
    CascStorageLocalFileCount = 0,
    CascStorageTotalFileCount = 1,
    CascStorageFeatures = 2,
    CascStorageInstalledLocales = 3,
    CascStorageProduct = 4,
    CascStorageTags = 5,
    CascStoragePathProduct = 6,
    CascStorageInfoClassMax = 7
}

/// <summary>Mirrors <c>CASC_STORAGE_PRODUCT</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Ansi)]
internal struct CASC_STORAGE_PRODUCT
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x1C)]
    public byte[] szCodeName;

    public uint BuildNumber;
}

/// <summary>Locale flag constants from <c>CascLib.h</c>.</summary>
internal static class CascLocale
{
    public const uint All = 0xFFFFFFFFu;
    public const uint None = 0x00000000u;
    public const uint Unknown1 = 0x00000001u;
    public const uint enUS = 0x00000002u;
    public const uint koKR = 0x00000004u;
    public const uint frFR = 0x00000010u;
    public const uint deDE = 0x00000020u;
    public const uint zhCN = 0x00000040u;
    public const uint esES = 0x00000080u;
    public const uint zhTW = 0x00000100u;
    public const uint enGB = 0x00000200u;
    public const uint esMX = 0x00001000u;
    public const uint ruRU = 0x00002000u;
    public const uint ptBR = 0x00004000u;
    public const uint itIT = 0x00008000u;
    public const uint ptPT = 0x00010000u;
}

/// <summary>Open-file flag constants from <c>CascLib.h</c>.</summary>
internal static class CascOpenFlags
{
    public const uint OpenByName = 0x00000000u;
    public const uint OpenByCKey = 0x00000001u;
    public const uint OpenByEKey = 0x00000002u;
    public const uint OpenByFileId = 0x00000003u;
    public const uint StrictDataCheck = 0x00000010u;
    public const uint OvercomeEncrypted = 0x00000020u;
}

/// <summary>Sentinel handle value (<c>INVALID_HANDLE_VALUE</c>) used by CascLib.</summary>
internal static class CascHandles
{
    public static readonly IntPtr InvalidHandle = new(-1);

    public static bool IsInvalid(IntPtr h) => h == IntPtr.Zero || h == InvalidHandle;
}
