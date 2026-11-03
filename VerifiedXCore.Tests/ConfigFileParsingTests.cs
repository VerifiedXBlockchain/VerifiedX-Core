using System;
using System.IO;
using VerifiedXCore;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// config.txt: a value keeps everything after the first '=', a line that is not key=value is skipped instead of
    /// stopping the node, the documented ChainCheckPoint key names are read, and extension lists accept entries with or
    /// without the leading dot. Reading still stops at the first blank line.
    /// </summary>
    [Collection("DbContextSequential")]
    public class ConfigFileParsingTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;

        public ConfigFileParsingTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
        }

        public void Dispose()
        {
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private static Config.Config Read(params string[] lines)
        {
            File.WriteAllLines(GetPathUtility.GetConfigPath() + "config.txt", lines);
            return Config.Config.ReadConfigFile();
        }

        [Fact]
        public void ValueContainingEquals_IsKeptWhole()
        {
            var config = Read("APIPassword=abc=def==", "WalletPassword=p=q");

            Assert.Equal("abc=def==", config.APIPassword);
            Assert.Equal("p=q", config.WalletPassword);
        }

        [Fact]
        public void LinesThatAreNotKeyValue_AreSkipped_AndTheRestIsRead()
        {
            var config = Read("WalletPassword=", "just some text", "=orphan", "APIPort=7444");

            Assert.Null(config.WalletPassword);
            Assert.Equal(7444, config.APIPort);
        }

        [Fact]
        public void DocumentedChainCheckPointKeys_AreRead()
        {
            var config = Read("ChainCheckPointInterval=4", "ChainCheckPointLocation=C:\\cp\\");

            Assert.Equal(4, config.ChainCheckPointInterval);
            Assert.Equal("C:\\cp\\", config.ChainCheckpointLocation);
        }

        [Fact]
        public void EarlierChainCheckPointKeySpellings_StillWork()
        {
            var config = Read("ChainCheckPointInternal=6", "ChainCheckpointLocation=D:\\cp\\");

            Assert.Equal(6, config.ChainCheckPointInterval);
            Assert.Equal("D:\\cp\\", config.ChainCheckpointLocation);
        }

        [Fact]
        public void RejectedExtensions_WithOrWithoutTheDot_MatchWhatGetExtensionReturns()
        {
            var config = Read("RejectAssetExtensionTypes=pdf,.TXT, png ,");

            Assert.Contains(Path.GetExtension("a.pdf"), config.RejectAssetExtensionTypes);
            Assert.Contains(".TXT", config.RejectAssetExtensionTypes); // kept as written: a case-sensitive check still matches a.TXT
            Assert.Contains(".png", config.RejectAssetExtensionTypes);
            Assert.DoesNotContain("", config.RejectAssetExtensionTypes);
            Assert.DoesNotContain("pdf", config.RejectAssetExtensionTypes);
        }

        [Fact]
        public void AllowedExtensions_WithOrWithoutTheDot_RemoveEveryRejection()
        {
            // .zip is in the built-in list and listed again by the operator: allowing it removes both.
            var config = Read("RejectAssetExtensionTypes=zip", "AllowedExtensionsTypes=zip,.DOC");

            Assert.DoesNotContain(".zip", config.RejectAssetExtensionTypes);
            Assert.DoesNotContain(".doc", config.RejectAssetExtensionTypes);
            Assert.Contains(".exe", config.RejectAssetExtensionTypes);
        }

        [Fact]
        public void ReadingStillStopsAtTheFirstBlankLine()
        {
            var config = Read("APIPort=7444", "", "APIPassword=after-blank");

            Assert.Equal(7444, config.APIPort);
            Assert.Null(config.APIPassword);
        }
    }
}
