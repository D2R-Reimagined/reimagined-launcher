using System.Text;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class D2RLoaderPluginVersionTests
{
    [Fact]
    public void ReadsStaticPluginInfoVersionWithoutLoadingDll()
    {
        Assert.Equal("0.2.14", ReadFixture(CreatePlugin()));
    }

    [Fact]
    public void UnsupportedGetterReturnsNoVersion()
    {
        var bytes = CreatePlugin();
        bytes[0x300] = 0xe9;
        Assert.Null(ReadFixture(bytes));
    }

    [Fact]
    public void InvalidVersionPointerReturnsNoVersion()
    {
        var bytes = CreatePlugin();
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(bytes, 0x398);
        Assert.Null(ReadFixture(bytes));
    }

    [Fact]
    public void InvalidDllReturnsNoVersion()
    {
        Assert.Null(ReadFixture([1, 2, 3]));
    }

    private static string? ReadFixture(byte[] bytes)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            return D2RLoaderPluginVersion.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreatePlugin()
    {
        var bytes = new byte[0x600];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);
        void At(int offset) => stream.Position = offset;
        At(0); writer.Write((ushort)0x5a4d);
        At(0x3c); writer.Write(0x80);
        At(0x80); writer.Write(0x4550);
        writer.Write((ushort)0x8664); writer.Write((ushort)1);
        At(0x94); writer.Write((ushort)0xf0); writer.Write((ushort)0x2022);
        At(0x98); writer.Write((ushort)0x20b);
        At(0xb0); writer.Write(0x180000000UL);
        writer.Write(0x1000); writer.Write(0x200);
        At(0xd0); writer.Write(0x2000); writer.Write(0x200);
        At(0x104); writer.Write(16);
        At(0x108); writer.Write(0x1000); writer.Write(0x80);
        At(0x188); writer.Write(Encoding.ASCII.GetBytes(".rdata\0\0"));
        writer.Write(0x400); writer.Write(0x1000); writer.Write(0x400); writer.Write(0x200);
        At(0x1ac); writer.Write(0x40000040);
        At(0x214); writer.Write(1); writer.Write(1);
        writer.Write(0x1040); writer.Write(0x1044); writer.Write(0x1048);
        At(0x240); writer.Write(0x1100); writer.Write(0x1050); writer.Write((ushort)0);
        At(0x250); writer.Write(Encoding.ASCII.GetBytes("D2RLoaderGetPluginInfo\0"));
        At(0x300); writer.Write(new byte[] { 0x48, 0x8d, 0x05 });
        writer.Write(0x1180 - 0x1107); writer.Write((byte)0xc3);
        At(0x380); writer.Write(72); writer.Write(1);
        At(0x398); writer.Write(0x1800011d0UL);
        At(0x3d0); writer.Write(Encoding.UTF8.GetBytes("0.2.14\0"));
        return bytes;
    }
}
