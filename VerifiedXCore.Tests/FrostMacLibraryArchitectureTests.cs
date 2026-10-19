using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// The macOS FROST library ships as one universal binary carrying both an x86_64 and an arm64 slice, so a node on an
    /// Intel Mac (the osx-x64 publish) loads it as an Apple Silicon node does.
    /// </summary>
    public class FrostMacLibraryArchitectureTests
    {
        private const uint FatMagic = 0xCAFEBABE;
        private const uint CpuTypeX86_64 = 0x01000007;
        private const uint CpuTypeArm64 = 0x0100000C;

        private static uint ReadBigEndian(BinaryReader reader)
        {
            var b = reader.ReadBytes(4);
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }

        private static List<uint> FatSliceCpuTypes(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            var cpuTypes = new List<uint>();
            if (ReadBigEndian(reader) != FatMagic)
                return cpuTypes;
            var count = ReadBigEndian(reader);
            for (var i = 0; i < count; i++)
            {
                cpuTypes.Add(ReadBigEndian(reader));
                reader.ReadBytes(16); // cpusubtype, offset, size, align
            }
            return cpuTypes;
        }

        [Fact]
        public void TheMacLibrary_CarriesBothIntelAndAppleSiliconSlices()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "libfrost_ffi.dylib");
            Assert.True(File.Exists(path), $"{path} is not in the build output");

            var cpuTypes = FatSliceCpuTypes(path);

            Assert.Contains(CpuTypeX86_64, cpuTypes);
            Assert.Contains(CpuTypeArm64, cpuTypes);
        }
    }
}
