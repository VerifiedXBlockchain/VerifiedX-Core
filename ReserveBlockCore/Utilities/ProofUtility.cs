using Newtonsoft.Json;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ReserveBlockCore.Utilities
{
    public class ProofUtility
    {
        private static readonly object ProofCacheLock = new object();
        private static long _proofCacheHeight = long.MinValue;
        private static List<Proof>? _allProofsCache;

        public static void ClearProofGenerationCache()
        {
            lock (ProofCacheLock)
            {
                _proofCacheHeight = long.MinValue;
                _allProofsCache = null;
            }
        }

        public static async Task<List<Proof>> GenerateProofs()
        {
            return await GetOrCreateAllProofsAsync();
        }

        private static async Task<List<Proof>> GetOrCreateAllProofsAsync()
        {
            var blockHeight = Globals.LastBlock.Height + 1;

            lock (ProofCacheLock)
            {
                if (_proofCacheHeight == blockHeight && _allProofsCache != null)
                    return _allProofsCache;
            }

            await BanService.RunUnban();

            lock (ProofCacheLock)
            {
                if (_proofCacheHeight == blockHeight && _allProofsCache != null)
                    return _allProofsCache;
            }

            List<Proof> proofs;
            if (Globals.IsBootstrapMode)
                proofs = await GenerateProofsFromNetworkValidatorsLegacy();
            else
            {
                proofs = await GenerateProofsFromSnapshotAsync();
                if (proofs.Count == 0)
                    proofs = await GenerateProofsFromNetworkValidatorsLegacy();
            }

            lock (ProofCacheLock)
            {
                if (_proofCacheHeight == blockHeight && _allProofsCache != null)
                    return _allProofsCache;
                if (blockHeight >= _proofCacheHeight)
                {
                    _allProofsCache = proofs;
                    _proofCacheHeight = blockHeight;
                    Globals.LastProofBlockheight = blockHeight;
                }
            }

            return proofs;
        }

        /// <summary>Legacy path: in-memory NetworkValidators (bootstrap only).</summary>
        private static async Task<List<Proof>> GenerateProofsFromNetworkValidatorsLegacy()
        {
            List<Proof> proofs = new List<Proof>();

            var blockHeight = Globals.LastBlock.Height + 1;
            var prevHash = Globals.LastBlock.Hash;

            var newPeers = Globals.NetworkValidators.Values.Where(x => x.CheckFailCount <= 3).ToList();

            List<string> CompletedIPs = new List<string>();
            List<string> CompletedAddresses = new List<string>();

            foreach (var val in newPeers)
            {
                if (val.Address != null && val.PublicKey != null)
                {
                    if (CompletedIPs.Contains(val.IPAddress) ||
                        CompletedAddresses.Contains(val.Address) ||
                        IsProducerExcluded(val.Address))
                        continue;

                    CompletedIPs.Add(val.IPAddress);
                    CompletedAddresses.Add(val.Address);

                    var stateAddress = StateData.GetSpecificAccountStateTrei(val.Address);
                    if (stateAddress == null)
                        continue;

                    if (stateAddress.Balance < ValidatorService.ValidatorRequiredAmount())
                        continue;

                    var proof = await CreateProof(val.Address, val.PublicKey, blockHeight, prevHash);
                    if (proof.Item1 != 0 && !string.IsNullOrEmpty(proof.Item2))
                    {
                        proofs.Add(new Proof
                        {
                            Address = val.Address,
                            BlockHeight = blockHeight,
                            PreviousBlockHash = prevHash,
                            ProofHash = proof.Item2,
                            PublicKey = val.PublicKey,
                            VRFNumber = proof.Item1,
                            IPAddress = val.IPAddress.Replace("::ffff:", "")
                        });
                    }
                }
            }

            return proofs;
        }

        /// <summary>Deterministic path: validator snapshot (post-bootstrap).</summary>
        private static async Task<List<Proof>> GenerateProofsFromSnapshotAsync()
        {
            List<Proof> proofs = new List<Proof>();
            var blockHeight = Globals.LastBlock.Height + 1;
            var prevHash = Globals.LastBlock.Hash;
            var snapshot = ValidatorSnapshotService.GetSnapshotForHeight(blockHeight);

            foreach (var entry in snapshot)
            {
                if (string.IsNullOrEmpty(entry.PublicKey))
                    continue;

                var proof = await CreateProof(entry.Address, entry.PublicKey, blockHeight, prevHash);
                if (proof.Item1 != 0 && !string.IsNullOrEmpty(proof.Item2))
                {
                    proofs.Add(new Proof
                    {
                        Address = entry.Address,
                        BlockHeight = blockHeight,
                        PreviousBlockHash = prevHash,
                        ProofHash = proof.Item2,
                        PublicKey = entry.PublicKey,
                        VRFNumber = proof.Item1,
                        IPAddress = (entry.IPAddress ?? "").Replace("::ffff:", "")
                    });
                }
            }

            return proofs;
        }

        /// <summary>
        /// Checks if a wallet version string has a major version matching Globals.MajorVer.
        /// Returns false for null/empty/malformed versions.
        /// </summary>
        internal static bool IsMajorVersionCurrent(string? walletVersion)
        {
            if (string.IsNullOrEmpty(walletVersion))
                return false;
            try
            {
                var parts = walletVersion!.Split('.');
                if (parts.Length < 1) return false;
                var major = Convert.ToInt32(parts[0]);
                return major >= Globals.MajorVer;
            }
            catch { return false; }
        }

        public static async Task<List<Proof>> GenerateCasterProofs()
        {
            var all = await GetOrCreateAllProofsAsync();

            // FIX: Filter BlockCasters to only include peers with current major wallet version.
            // This prevents phantom casters on outdated versions from inflating the proof count
            // and potentially winning VRF elections they can't fulfil.
            //
            // HYDRATE: Newly-promoted casters (see CasterDiscoveryService.HandlePromotion) may
            // enter BlockCasters with an empty WalletVersion because the signed CasterInfo
            // payload doesn't carry a version. Without this hydration they'd be silently
            // filtered out here, producing casterProofs=0 and permanently blocking a newly
            // promoted node from casting blocks.
            //
            // For our own entry we always know the version. For peers with an IP we do a
            // best-effort HTTP fetch of /valapi/validator/GetWalletVersion (same endpoint
            // used by CasterDiscoveryService.CheckCandidateVersionDetailed). On success the
            // result is cached directly on the Peers object; on failure we skip this caster
            // for this round and try again next tick.
            foreach (var c in Globals.BlockCasters)
            {
                if (string.IsNullOrEmpty(c.ValidatorAddress))
                    continue;
                if (!string.IsNullOrEmpty(c.WalletVersion))
                    continue;

                if (c.ValidatorAddress == Globals.ValidatorAddress)
                {
                    c.WalletVersion = Globals.CLIVersion;
                    continue;
                }

                if (string.IsNullOrEmpty(c.PeerIP))
                    continue;

                try
                {
                    using var verClient = Globals.HttpClientFactory.CreateClient();
                    using var verCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var verUri = $"http://{c.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/GetWalletVersion";
                    var verResp = await verClient.GetAsync(verUri, verCts.Token);
                    if (verResp.IsSuccessStatusCode)
                    {
                        var ver = await verResp.Content.ReadAsStringAsync();
                        if (!string.IsNullOrEmpty(ver))
                        {
                            c.WalletVersion = ver.Trim();
                            CasterLogUtility.Log(
                                $"Hydrated WalletVersion for caster {c.ValidatorAddress} at {c.PeerIP}: '{c.WalletVersion}'",
                                "PROOFS-HYDRATE");
                        }
                    }
                }
                catch { /* best-effort — will retry next proof generation cycle */ }
            }

            var validCasters = Globals.BlockCasters
                .Where(c => !string.IsNullOrEmpty(c.ValidatorAddress))
                .Where(c => IsMajorVersionCurrent(c.WalletVersion))
                .ToList();
            
            var casterAddrs = new HashSet<string>(
                validCasters.Select(c => c.ValidatorAddress!),
                StringComparer.Ordinal);
            var list = all.Where(p => casterAddrs.Contains(p.Address)).ToList();

            // DIAGNOSTIC: Log detail when casterProofs=0 but allProofs exist, to identify pool/caster mismatch
            if (list.Count == 0 && all.Count > 0)
            {
                var allAddrs = string.Join(", ", all.Select(p => p.Address).Distinct().Take(10));
                var casterAddrList = string.Join(", ", casterAddrs.Take(10));
                var totalBlockCasters = Globals.BlockCasters.Count;
                var castersWithAddr = Globals.BlockCasters.Count(c => !string.IsNullOrEmpty(c.ValidatorAddress));
                var netValCount = Globals.NetworkValidators.Count;
                CasterLogUtility.Log(
                    $"casterProofs=0 DIAGNOSTIC: allProofs={all.Count} proofAddrs=[{allAddrs}] " +
                    $"casterAddrs=[{casterAddrList}] BlockCasters.Total={totalBlockCasters} " +
                    $"BlockCasters.WithAddr={castersWithAddr} NetworkValidators={netValCount}",
                    "PROOFS-DIAG");
            }

            // Snapshot / NetworkValidators often omit seed casters; build missing proofs from agreed BlockCasters (pubkey on peer).
            var blockHeight = Globals.LastBlock.Height + 1;
            var prevHash = Globals.LastBlock.Hash;
            foreach (var peer in validCasters)
            {
                if (string.IsNullOrEmpty(peer.ValidatorAddress))
                    continue;
                
                // Try to populate missing public key from NetworkValidators or via HTTP
                if (string.IsNullOrEmpty(peer.ValidatorPublicKey))
                {
                    if (Globals.NetworkValidators.TryGetValue(peer.ValidatorAddress, out var nv) && !string.IsNullOrEmpty(nv.PublicKey))
                    {
                        peer.ValidatorPublicKey = nv.PublicKey;
                    }
                    else
                    {
                        // Fetch public key from the remote caster's ValidatorInfo endpoint
                        try
                        {
                            if (!string.IsNullOrEmpty(peer.PeerIP))
                            {
                                using var client = Globals.HttpClientFactory.CreateClient();
                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                                var uri = $"http://{peer.PeerIP.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/ValidatorInfo";
                                var response = await client.GetAsync(uri, cts.Token);
                                if (response.IsSuccessStatusCode)
                                {
                                    var infoStr = await response.Content.ReadAsStringAsync();
                                    if (!string.IsNullOrEmpty(infoStr) && infoStr.Contains(","))
                                    {
                                        var parts = infoStr.Split(',');
                                        if (parts.Length >= 2)
                                        {
                                            var remoteAddress = parts[0].Trim();
                                            var remotePubKey = parts[1].Trim();
                                            if (!string.IsNullOrEmpty(remotePubKey))
                                            {
                                                peer.ValidatorPublicKey = remotePubKey;
                                                if (string.IsNullOrEmpty(peer.ValidatorAddress))
                                                    peer.ValidatorAddress = remoteAddress;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { /* best-effort */ }
                        
                        if (string.IsNullOrEmpty(peer.ValidatorPublicKey))
                            continue;
                    }
                }
                
                if (list.Exists(p => p.Address == peer.ValidatorAddress))
                    continue;

                var stateAddress = StateData.GetSpecificAccountStateTrei(peer.ValidatorAddress);
                if (stateAddress == null || stateAddress.Balance < ValidatorService.ValidatorRequiredAmount())
                    continue;

                var proofTuple = await CreateProof(peer.ValidatorAddress, peer.ValidatorPublicKey, blockHeight, prevHash);
                if (proofTuple.Item1 != 0 && !string.IsNullOrEmpty(proofTuple.Item2))
                {
                    list.Add(new Proof
                    {
                        Address = peer.ValidatorAddress,
                        BlockHeight = blockHeight,
                        PreviousBlockHash = prevHash,
                        ProofHash = proofTuple.Item2,
                        PublicKey = peer.ValidatorPublicKey,
                        VRFNumber = proofTuple.Item1,
                        IPAddress = (peer.PeerIP ?? "").Replace("::ffff:", "")
                    });
                }
            }

            return list;
        }
        public static async Task<(uint, string)> CreateProof(string address, string publicKey, long blockHeight, string prevBlockHash)
        {

            uint vrfNum = 0;
            var proof = "";
            // Random seed
            string seed = publicKey + blockHeight.ToString() + prevBlockHash;

            // Convert the combined input to bytes (using UTF-8 encoding)
            byte[] combinedBytes = Encoding.UTF8.GetBytes(seed);

            // Calculate a hash using SHA256
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(combinedBytes);

                //Produces non-negative by shifting and masking 
                int randomBytesAsInt = BitConverter.ToInt32(hashBytes, 0);
                uint nonNegativeRandomNumber = (uint)(randomBytesAsInt & 0x7FFFFFFF);

                vrfNum = nonNegativeRandomNumber;
                proof = ProofUtility.CalculateSHA256Hash(seed + vrfNum.ToString());
            }

            return (vrfNum, proof);
        }

        public static async Task<bool> VerifyProofAsync(string publicKey, long blockHeight, string prevBlockHash, string proofHash)
        {
            try
            {
                uint vrfNum = 0;
                var proof = "";
                // Random seed
                string seed = publicKey + blockHeight.ToString() + prevBlockHash;
                //if (Globals.BlockHashes.Count >= 35)
                //{
                //    var height = blockHeight - 7;
                //    seed = seed + Globals.BlockHashes[height].ToString();
                //}

                // Convert the combined input to bytes (using UTF-8 encoding)
                byte[] combinedBytes = Encoding.UTF8.GetBytes(seed);

                // Calculate a hash using SHA256
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(combinedBytes);

                    //Produces non-negative by shifting and masking 
                    int randomBytesAsInt = BitConverter.ToInt32(hashBytes, 0);
                    uint nonNegativeRandomNumber = (uint)(randomBytesAsInt & 0x7FFFFFFF);

                    vrfNum = nonNegativeRandomNumber;
                    proof = ProofUtility.CalculateSHA256Hash(seed + vrfNum.ToString());

                    if (proof == proofHash)
                        return true;
                }

                return false;
            }
            catch { return false; }

        }

        public static bool VerifyProof(string publicKey, long blockHeight, string prevBlockHash, string proofHash)
        {
            try
            {
                uint vrfNum = 0;
                var proof = "";
                // Random seed
                string seed = publicKey + blockHeight.ToString() + prevBlockHash;
                //if (Globals.BlockHashes.Count >= 35)
                //{
                //    var height = blockHeight - 7;
                //    seed = seed + Globals.BlockHashes[height].ToString();
                //}
                // Convert the combined input to bytes (using UTF-8 encoding)
                byte[] combinedBytes = Encoding.UTF8.GetBytes(seed);

                // Calculate a hash using SHA256
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(combinedBytes);

                    //Produces non-negative by shifting and masking 
                    int randomBytesAsInt = BitConverter.ToInt32(hashBytes, 0);
                    uint nonNegativeRandomNumber = (uint)(randomBytesAsInt & 0x7FFFFFFF);

                    vrfNum = nonNegativeRandomNumber;
                    proof = ProofUtility.CalculateSHA256Hash(seed + vrfNum.ToString());

                    if (proof == proofHash)
                        return true;
                }

                return false;
            }
            catch { return false; }

        }

        public static async Task<(bool, Block?)> VerifyWinnerAvailability(Proof winningProof, long nextBlock)
        {
            using (var client = Globals.HttpClientFactory.CreateClient())
            {
                try
                {
                    //skip call because current winner is our node.
                    if (winningProof.Address == Globals.ValidatorAddress)
                    {
                        return (true, null);
                    }

                    var cleanIP = winningProof.IPAddress.Replace("::ffff:", "");

                    // FIX B: Version gate — reject validators on outdated versions.
                    // Old nodes won't have the GetWalletVersion endpoint → 404 → rejected.
                    try
                    {
                        var versionUri = $"http://{cleanIP}:{Globals.ValAPIPort}/valapi/validator/GetWalletVersion";
                        var versionResp = await client.GetAsync(versionUri).WaitAsync(new TimeSpan(0, 0, 3));
                        if (versionResp == null || !versionResp.IsSuccessStatusCode)
                        {
                            CasterLogUtility.Log($"VersionGate: Winner {winningProof.Address} at {cleanIP} — GetWalletVersion returned {versionResp?.StatusCode}. Rejecting.", "VERSIONGATE");
                            return (false, null);
                        }
                        var peerVersion = await versionResp.Content.ReadAsStringAsync();
                        if (string.IsNullOrEmpty(peerVersion) || !IsMajorVersionCurrent(peerVersion))
                        {
                            CasterLogUtility.Log($"VersionGate: Winner {winningProof.Address} at {cleanIP} reports version '{peerVersion}' — outdated (need major >= {Globals.MajorVer}). Rejecting.", "VERSIONGATE");
                            return (false, null);
                        }
                    }
                    catch (Exception vex)
                    {
                        CasterLogUtility.Log($"VersionGate: Winner {winningProof.Address} at {cleanIP} — version check failed: {vex.Message}. Rejecting.", "VERSIONGATE");
                        return (false, null);
                    }

                    var uri = $"http://{cleanIP}:{Globals.ValAPIPort}/valapi/validator/VerifyBlock/{nextBlock}/{winningProof.ProofHash}";
                    var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 3));

                    if (response != null)
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            return (true, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                }
            }

            return (false, null);
        }

        public static async Task<(bool, Block?)> VerifyValAvailability(string ip, string winningAddress, long nextBlock)
        {
            using (var client = Globals.HttpClientFactory.CreateClient())
            {
                try
                {
                    //skip call because current winner is our node.
                    if (winningAddress == Globals.ValidatorAddress)
                    {
                        return (true, Globals.NextValidatorBlock);
                    }

                    var cleanIP = ip.Replace("::ffff:", "");

                    // FIX C: Version gate for block-crafting validators too.
                    try
                    {
                        var versionUri = $"http://{cleanIP}:{Globals.ValAPIPort}/valapi/validator/GetWalletVersion";
                        var versionResp = await client.GetAsync(versionUri).WaitAsync(new TimeSpan(0, 0, 3));
                        if (versionResp == null || !versionResp.IsSuccessStatusCode)
                            return (false, null);
                        var peerVersion = await versionResp.Content.ReadAsStringAsync();
                        if (string.IsNullOrEmpty(peerVersion) || !IsMajorVersionCurrent(peerVersion))
                            return (false, null);
                    }
                    catch { return (false, null); }

                    var uri = $"http://{cleanIP}:{Globals.ValAPIPort}/valapi/validator/VerifyBlock/{nextBlock}/aaa";
                    var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 2));

                    if (response != null)
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            var blockJson = await response.Content.ReadAsStringAsync();
                            if (blockJson == null)
                                return (false, null);

                            var block = JsonConvert.DeserializeObject<Block>(blockJson);

                            if (block == null)
                                return (false, null);

                            return (true, block);
                        }
                    }
                }
                catch (Exception ex)
                {
                }
            }

            return (false, null);
        }

        public static async Task<Proof?> SortProofs(List<Proof> proofs, bool isWinnerList = false)
        {
            try
            {
                var processHeight = Globals.LastBlock.Height + 1;

                var validProofs = proofs.Where(p =>
                    p.BlockHeight == processHeight &&
                    p.PreviousBlockHash == Globals.LastBlock.Hash &&
                    p.VerifyProof() &&
                    !Globals.ABL.Exists(x => x == p.Address)
                ).ToList();

                // Sort deterministically by VRF number with tiebreaking
                return validProofs
                    .OrderBy(x => x.VRFNumber)  // Closest to zero wins
                    .ThenBy(x => x.ProofHash)   // Tiebreak by proof hash
                    .ThenBy(x => x.Address)     // Final tiebreak by address
                    .FirstOrDefault();

            }
            catch { return null; }
        }

        public static void AddFailedProducer(string address)
        {
            var currentTime = TimeUtil.GetMillisecondTime();
            Globals.FailedBlockProducers.AddOrUpdate(
                address,
                (addr) => (1, currentTime),
                (addr, existing) => (existing.failCount + 1, currentTime)
            );

            ConsoleWriterService.OutputVal($"\r\nAddress: {address} added to failed block producers. (Globals.FailedBlockProducers)");
        }

        public static void PruneFailedProducers()
        {
            var currentTime = TimeUtil.GetMillisecondTime();
            foreach (var val in Globals.FailedBlockProducers)
            {
                var timeSinceFailure = currentTime - val.Value.lastFailTime;

                // Remove from exclusion list if enough time has passed
                if (timeSinceFailure > Globals.BlockTime * 100) // Exclude for 100 block times
                {
                    Globals.FailedBlockProducers.TryRemove(val.Key, out _);
                }
            }
        }

        // Add method to check and clean failed producers
        private static bool IsProducerExcluded(string address)
        {
            if (Globals.FailedBlockProducers.TryGetValue(address, out var failureInfo))
            {
                var currentTime = TimeUtil.GetMillisecondTime();
                var timeSinceFailure = currentTime - failureInfo.lastFailTime;

                // Remove from exclusion list if enough time has passed
                if (timeSinceFailure > Globals.BlockTime * 100) // Exclude for 100 block times
                {
                    Globals.FailedBlockProducers.TryRemove(address, out _);
                    return false;
                }

                // Exclude if they've failed multiple times recently
                return failureInfo.failCount >= 2;
            }
            return false;
        }

        public static string CalculateSHA256Hash(string input)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hashBytes);
            }
        }
    }
}
