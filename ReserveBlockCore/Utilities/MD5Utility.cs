using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Utilities
{
    public class MD5Utility
    {
        /// <summary>
        /// True when an MD5List refers to media that actually ships with the contract.
        ///
        /// Two markers mean "no media": the "NA" sentinel, and the built-in vBTC
        /// placeholder image. Default-asset vBTC contracts are stamped at mint with
        /// "defaultvBTC.png::&lt;md5&gt;" (see VBTCController Mint) rather than "NA", and that
        /// file is generated locally from DefaultVBTCLogo() — it is never uploaded to a
        /// beacon, so transfers of those contracts must not wait on one.
        /// </summary>
        public static bool HasMedia(string? md5List)
        {
            if (string.IsNullOrWhiteSpace(md5List) || md5List == "NA")
                return false;

            // MD5ListCreator joins entries with "<>" as "assetName::checksum".
            var entries = md5List.Split("<>", StringSplitOptions.RemoveEmptyEntries);

            return entries.Any(x => !x.Trim().StartsWith("defaultvBTC", StringComparison.OrdinalIgnoreCase));
        }

        public static async Task<string> GetMD5FromSmartContract(SmartContractMain sc)
        {
            List<string> assets = new List<string>();

            assets = await NFTAssetFileUtility.GetAssetListFromSmartContract(sc);

            var md5List = MD5ListCreator(assets, sc.SmartContractUID);

            return md5List;
        }

        public static string MD5ListCreator(List<string> assets, string scUID)
        {
            var output = "";

            var checksumList = "";

            foreach(var asset in assets)
            {
                var path = NFTAssetFileUtility.NFTAssetPath(asset, scUID);

                if (path == "NA")
                    continue; //asset file does not exist locally; hashing the sentinel path would throw

                var checksum = path.ToMD5();

                if(checksumList == "")
                {
                    var checksumAsset = asset + "::" + checksum;
                    checksumList = checksumAsset;
                }
                else
                {
                    var checksumAsset = asset + "::" + checksum;
                    checksumList = checksumList + "<>" + checksumAsset;
                }
            }

            output = checksumList;

            return output;
        }
        public static async Task<List<string>> GetAssetList(string md5List)
        {
            List<string> assetList = new List<string>();
            var md5Split = md5List.Split(new string[] { "<>" }, StringSplitOptions.None);

            foreach(var md5 in md5Split)
            {
                var md5AssetSplit = md5.Split(new string[] { "::" }, StringSplitOptions.None);
                assetList.Add(md5AssetSplit[0]);
            }

            return assetList;
        }
    }
}
