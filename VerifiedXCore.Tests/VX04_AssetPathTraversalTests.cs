using System;
using System.IO;
using System.Threading.Tasks;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Models.DST;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// VX-04 (HIGH): "One UDP datagram reads arbitrary files and exfiltrates the validator private key".
    ///
    /// Audit PoC: MessageType 18 (AssetReq) with Data "uid1,../../../CANARY_SECRET.txt,xpoc,0" made the
    /// node read a file outside its asset folder and return it in UDP packets; walking the packet index
    /// read the wallet database. The asset name and contract UID were concatenated into a path.
    /// </summary>
    [Collection("DbContextSequential")]
    public class VX04_AssetPathTraversalTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _priorCustomPath;
        private readonly string _canaryDir;
        private readonly string _contractFolderUid;

        public VX04_AssetPathTraversalTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"vx04_test_{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(_tempRoot);
            _priorCustomPath = Globals.CustomPath;
            Globals.CustomPath = _tempRoot;
            DbContext.Initialize();

            // A canary OUTSIDE the requested contract's asset folder but inside the node's asset tree,
            // reachable from "<assets>/<contract>/thumbs/" with "../../<canaryDir>/CANARY_SECRET.txt".
            var unique = Guid.NewGuid().ToString("N");
            _contractFolderUid = "vx04req" + unique;
            var probe = NFTAssetFileUtility.CreateNFTAssetPath("probe.jpg", _contractFolderUid, thumbs: true);
            var assetsRoot = Directory.GetParent(Path.GetDirectoryName(Path.GetDirectoryName(probe)!)!)!.FullName;
            _canaryDir = Path.Combine(assetsRoot, "vx04canary" + unique);
            Directory.CreateDirectory(_canaryDir);
            File.WriteAllText(Path.Combine(_canaryDir, "CANARY_SECRET.txt"), "CANARY-DO-NOT-DISCLOSE");
        }

        public void Dispose()
        {
            try { Directory.Delete(_canaryDir, recursive: true); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(NFTAssetFileUtility.CreateNFTAssetPath("probe.jpg", _contractFolderUid, true))!)!, recursive: true); } catch { }
            try { DbContext.CloseDB(); } catch { }
            Globals.CustomPath = _priorCustomPath;
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        private string CanaryTraversal => "../../" + Path.GetFileName(_canaryDir) + "/CANARY_SECRET.txt";

        [Fact]
        public void VX04_AuditPoC_TraversalName_ResolvesToNothing_EvenThoughTheFileExists()
        {
            Assert.True(File.Exists(Path.Combine(_canaryDir, "CANARY_SECRET.txt")));
            Assert.Equal("NA", NFTAssetFileUtility.NFTAssetPath(CanaryTraversal, _contractFolderUid, getThumbs: true));
            Assert.Equal("NA", NFTAssetFileUtility.NFTAssetPath(CanaryTraversal.Replace('/', '\\'), _contractFolderUid, getThumbs: true));
        }

        [Fact]
        public void VX04_AuditPoC_LiteralPayload_ResolvesToNothing()
        {
            Assert.Equal("NA", NFTAssetFileUtility.NFTAssetPath("../../../CANARY_SECRET.txt", "xpoc", getThumbs: true));
            Assert.Equal("NA", NFTAssetFileUtility.NFTAssetPath("../../../ConfigTestNet/config.txt", "xpoc", getThumbs: true));
        }

        [Fact]
        public void VX04_TraversalThroughTheContractUid_ResolvesToNothing()
        {
            // "<assets>/x/../<canaryDir>/" is the canary folder.
            var uidTraversal = "x/../" + Path.GetFileName(_canaryDir);
            Assert.Equal("NA", NFTAssetFileUtility.NFTAssetPath("CANARY_SECRET.txt", uidTraversal, getThumbs: false));
        }

        [Fact]
        public void VX04_Control_LegitimateAsset_Resolves()
        {
            var path = NFTAssetFileUtility.CreateNFTAssetPath("legit.jpg", _contractFolderUid, thumbs: true);
            File.WriteAllText(path, "thumb");
            try
            {
                Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(NFTAssetFileUtility.NFTAssetPath("legit.jpg", _contractFolderUid, getThumbs: true)));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void VX04_ControlMissingFile_IsNA() =>
            Assert.Equal("NA", NFTAssetFileUtility.NFTAssetPath("nosuchfile.jpg", _contractFolderUid, getThumbs: true));

        [Theory]
        [InlineData("../x.jpg")]
        [InlineData("..\\x.jpg")]
        [InlineData("a/b.jpg")]
        [InlineData("a\\b.jpg")]
        [InlineData("C:\\Windows\\win.ini")]
        [InlineData("/etc/passwd")]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("x\0.jpg")]
        [InlineData("file.jpg:stream")]
        [InlineData("")]
        public void VX04_UnsafeNames_AreRejected_OnReadAndWrite(string name)
        {
            Assert.False(NFTAssetFileUtility.IsSafeAssetFileName(name));
            Assert.Equal("NA", NFTAssetFileUtility.NFTAssetPath(name, _contractFolderUid, getThumbs: true));
            // Write side: names from a remote shop / beacon are refused instead of written.
            Assert.Throws<ArgumentException>(() => NFTAssetFileUtility.CreateNFTAssetPath(name, _contractFolderUid, thumbs: true));
        }

        [Theory]
        [InlineData("my asset (1).png")]
        [InlineData("vbtc_v2_token")]
        [InlineData("defaultvBTC.png")]
        [InlineData("ünïcödé.jpg")]
        public void VX04_OrdinaryNames_AreAccepted(string name) => Assert.True(NFTAssetFileUtility.IsSafeAssetFileName(name));

        // ── Request authorization: listed contract + listed asset only ─────────────────────────

        private void ListContractWithAsset(string scUid, string assetName)
        {
            SmartContractMain.SmartContractData.SaveSmartContract(new SmartContractMain
            {
                SmartContractUID = scUid,
                Name = "listed",
                MinterAddress = "xMinter",
                SmartContractAsset = new SmartContractAsset { Name = assetName, Location = "default", FileSize = 1 },
            }, null);
            Listing.GetListingDb()!.Insert(new Listing { SmartContractUID = scUid, AddressOwner = "xMinter", PurchaseKey = "k" });
        }

        [Fact]
        public async Task VX04_Request_ForListedAssetThumbnail_IsAuthorized()
        {
            ListContractWithAsset("listed:1", "art.png");
            Assert.True(await VerifiedXCore.DST.MessageService.IsListedAssetThumbnail("listed:1", "art.jpg"));
        }

        [Fact]
        public async Task VX04_Request_ForTraversalName_OnListedContract_IsNotAuthorized()
        {
            ListContractWithAsset("listed:2", "art.png");
            Assert.False(await VerifiedXCore.DST.MessageService.IsListedAssetThumbnail("listed:2", "../../../CANARY_SECRET.txt"));
            Assert.False(await VerifiedXCore.DST.MessageService.IsListedAssetThumbnail("listed:2", "other.jpg"));
        }

        [Fact]
        public async Task VX04_Request_ForUnlistedContract_IsNotAuthorized()
        {
            SmartContractMain.SmartContractData.SaveSmartContract(new SmartContractMain
            {
                SmartContractUID = "unlisted:1", Name = "x", MinterAddress = "xMinter",
                SmartContractAsset = new SmartContractAsset { Name = "art.png", Location = "default", FileSize = 1 },
            }, null);
            Assert.False(VerifiedXCore.DST.MessageService.IsListedOnThisShop("unlisted:1"));
            Assert.False(await VerifiedXCore.DST.MessageService.IsListedAssetThumbnail("unlisted:1", "art.jpg"));
        }

        [Theory]
        [InlineData("art.png", "art.jpg")]
        [InlineData("art.jpg", "art.jpg")]
        [InlineData("doc.pdf", "doc.jpg")]
        [InlineData("clip.mp4", null)]
        public void VX04_ThumbnailRequestName_MatchesTheBuyerMapping(string asset, string? expected) =>
            Assert.Equal(expected, NFTAssetFileUtility.ThumbnailRequestName(asset));
    }
}
