using ReimaginedLauncher.Utilities.Casc;
using System.Runtime.InteropServices;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class CascNativeInteropTests
{
    // Expected offsets are CascLib.h's CASC_FIND_DATA with Windows MAX_PATH (260) and CascPort.h's non-Windows MAX_PATH (1024).
    [Theory]
    [InlineData(false, 260, 344)]
    [InlineData(true, 1024, 1104)]
    public void FindDataLayoutMatchesTheNativeCascLibBuild(bool posix, int maxPath, int size)
    {
        var type = posix ? typeof(CASC_FIND_DATA_POSIX) : typeof(CASC_FIND_DATA);
        var tagBitMask = (maxPath + 32 + 7) / 8 * 8;

        Assert.Equal(size, Marshal.SizeOf(type));
        Assert.Equal(maxPath, (int)Marshal.OffsetOf(type, "CKey"));
        Assert.Equal(maxPath + 16, (int)Marshal.OffsetOf(type, "EKey"));
        Assert.Equal(tagBitMask, (int)Marshal.OffsetOf(type, "TagBitMask"));
        Assert.Equal(tagBitMask + 8, (int)Marshal.OffsetOf(type, "FileSize"));
        Assert.Equal(tagBitMask + 24, (int)Marshal.OffsetOf(type, "dwFileDataId"));
        Assert.Equal(tagBitMask + 28, (int)Marshal.OffsetOf(type, "dwLocaleFlags"));
        Assert.Equal(tagBitMask + 32, (int)Marshal.OffsetOf(type, "dwContentFlags"));
    }

    [Fact]
    public void TheFindBufferMatchesThisPlatformsCascLibBuild()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? 344 : 1104, NativeCascLib.FindDataSize);
    }

    [Fact]
    public void TheVendoredCascLibLoadsOnWindowsAndLinuxX64()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            return;
        }

        var native = new NativeCascLib();

        Assert.True(native.IsAvailable, native.UnavailableReason);
    }
}
