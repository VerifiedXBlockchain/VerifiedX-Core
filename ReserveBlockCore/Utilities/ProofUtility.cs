using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.Services;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ReserveBlockCore.Utilities
{
    public class ProofUtility
    {
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public static async Task<List<Proof>> GenerateProofs()
        {
            List<Proof> proofs = new List<Proof>();

            var blockHeight = Globals.LastBlock.Height + 1;
            var prevHash = Globals.LastBlock.Hash;

            var peerDB = Peers.GetAll();
            //Force unban quicker
            await BanService.RunUnban();

            var SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys)
                .Union(Globals.SkipValPeers.Keys)
                .Union(Globals.ReportedIPs.Keys));

            if (Globals.ValidatorAddress == "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC")
            {
                SkipIPs.Add("144.126.156.101");
            }

            var newPeers = peerDB.Find(x => x.IsValidator).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray();

            if (!newPeers.Any())
            {
                //clear out skipped peers to try again
                Globals.SkipValPeers.Clear();

                SkipIPs = new HashSet<string>(Globals.ValidatorNodes.Values.Select(x => x.NodeIP.Replace(":" + Globals.Port, ""))
                .Union(Globals.BannedIPs.Keys)
                .Union(Globals.SkipValPeers.Keys)
                .Union(Globals.ReportedIPs.Keys));

                newPeers = peerDB.Find(x => x.IsValidator).ToArray()
                .Where(x => !SkipIPs.Contains(x.PeerIP))
                .ToArray();
            }

            List<Peers> peersMissingDataList = new List<Peers>();

            foreach(var val in newPeers)
            {
                if(val.ValidatorAddress != null && val.ValidatorPublicKey != null)
                {
                    var stateAddress = StateData.GetSpecificAccountStateTrei(val.ValidatorAddress);
                    if (stateAddress == null)
                    {
                        continue;
                    }

                    if (stateAddress.Balance < ValidatorService.ValidatorRequiredAmount())
                    {
                        continue;
                    }

                    var proof = await CreateProof(val.ValidatorAddress, val.ValidatorPublicKey, blockHeight, prevHash);
                    if (proof.Item1 != 0 && !string.IsNullOrEmpty(proof.Item2))
                    {
                        Proof _proof = new Proof
                        {
                            Address = val.ValidatorAddress,
                            BlockHeight = blockHeight,
                            PreviousBlockHash = prevHash,
                            ProofHash = proof.Item2,
                            PublicKey = val.ValidatorPublicKey,
                            VRFNumber = proof.Item1,
                            IPAddress = val.PeerIP
                        };

                        proofs.Add(_proof);
                    }
                }
                else
                {
                    peersMissingDataList.Add(val);
                }
            }

            Globals.LastProofBlockheight = blockHeight;

            return proofs;
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

        public static async Task<Proof?> SortProofs(List<Proof> proofs, bool isWinnerList = false)
        {
            try
            {
                var processHeight = Globals.LastBlock.Height + 1;
                var finalProof = new Proof();
                var currentWinningProof = proofs.FirstOrDefault();
                foreach (var proof in proofs)
                {
                    if(Globals.ABL.Exists(x => x == proof.Address))
                    {
                        continue;
                    }

                    if (currentWinningProof != null)
                    {
                        if (proof.VerifyProof())
                        {
                            if (processHeight != proof.BlockHeight)
                                continue;

                            //Closer to zero wins.
                            if (currentWinningProof.VRFNumber > proof.VRFNumber)
                            {
                                using (var client = Globals.HttpClientFactory.CreateClient())
                                {
                                    try
                                    {
                                        var uri = $"http://{proof.IPAddress}:{Globals.ValPort}/api/validator/heartbeat";
                                        var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 1));

                                        if (response != null)
                                        {
                                            if (response.IsSuccessStatusCode)
                                                finalProof = proof;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                    }
                                    
                                }
                                
                            }
                        }
                        else
                        {
                            //stop checking due to proof failure. This should never happen unless a rigged proof is entered. 
                            continue;
                        }
                    }
                }

                return finalProof;
            }
            catch { return null; }
        }

        public static async Task AddProof(long blockHeight, Proof proof)
        {
            var currentBlockHeight = Globals.LastBlock.Height - 5;

            if(currentBlockHeight <= blockHeight)
                Globals.WinningProofs.TryAdd(proof.BlockHeight, proof);
        }

        public static async Task AddBackupProof(long blockHeight, List<Proof> proof)
        {
            var currentBlockHeight = Globals.LastBlock.Height - 5;

            if (currentBlockHeight <= blockHeight)
                Globals.BackupProofs.TryAdd(blockHeight, proof);
        }

        public static async Task AbandonProof(long height, string supposeValidatorAddress)
        {
            await _semaphore.WaitAsync();
            try
            {
                int maxRetries = 10;
                int counter = 0;
                while (!Globals.FinalizedWinner.TryRemove(height, out _) && counter < maxRetries)
                {
                    counter++;
                    await Task.Delay(20);
                }

                counter = 0;
                var blockDiff = (TimeUtil.GetTime() - Globals.LastBlockAddedTimestamp);
                if (blockDiff >= 120)
                {
                    Globals.FailedValidators.Clear();
                    Globals.FailedValidators = new ConcurrentDictionary<string, long>();
                }
                    
                Globals.FailedValidators.TryAdd(supposeValidatorAddress, height + 1);

                var proofList = Globals.WinningProofs;
                foreach (var proof in proofList)
                {
                    if (proof.Value.Address == supposeValidatorAddress)
                    {
                        while (!Globals.WinningProofs.TryRemove(proof.Key, out _) && counter < maxRetries)
                        {
                            counter++;
                            await Task.Delay(100);
                        }
                        counter = 0;
                        Globals.BackupProofs.TryGetValue(proof.Key, out var backupProofList);
                        if (backupProofList != null)
                        {
                            var newProof = backupProofList.Where(x => x.Address != supposeValidatorAddress).OrderBy(x => x.VRFNumber).FirstOrDefault();
                            if (newProof != null)
                            {
                                while (!Globals.WinningProofs.TryAdd(proof.Key, newProof) && counter < maxRetries)
                                {
                                    counter++;
                                    await Task.Delay(20);
                                }
                                counter = 0;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {

            }
            finally
            {
                _semaphore.Release();
            }
            ValidatorLogUtility.Log($"Validator {supposeValidatorAddress} failed to produce block for height: {height} ", "ProofUtility.AbandonProof()");
        }

        public static async Task ProofCleanup()
        {
            var blockHeight = Globals.LastBlock.Height;

            var keysToRemove = Globals.WinningProofs.Where(x => x.Key < blockHeight).ToList();

            var backupKeysToRemove = Globals.BackupProofs.Where(x => x.Key < blockHeight).ToList();

            var networkBlockQueueToRemove = Globals.NetworkBlockQueue.Where(x => x.Key < blockHeight).ToList();

            foreach (var key in keysToRemove)
            {
                try
                {
                    var proofCountRemove = 0;
                    bool removed = false;

                    while (!removed && proofCountRemove < 10)
                    {
                        if (Globals.WinningProofs.ContainsKey(key.Key))
                        {
                            removed = Globals.WinningProofs.TryRemove(key.Key, out _);
                        }

                        if (!removed)
                        {
                            proofCountRemove++;
                            await Task.Delay(20);
                        }
                    }
                }
                catch { }
            }

            foreach (var key in backupKeysToRemove)
            {
                try
                {
                    var backupProofCountRemove = 0;
                    bool removed = false;

                    while (!removed && backupProofCountRemove < 10)
                    {
                        if (Globals.BackupProofs.ContainsKey(key.Key))
                        {
                            removed = Globals.BackupProofs.TryRemove(key.Key, out _);
                        }

                        if (!removed)
                        {
                            backupProofCountRemove++;
                            await Task.Delay(20);
                        }
                    }
                }
                catch { }
            }

            foreach (var key in networkBlockQueueToRemove)
            {
                try
                {
                    var networkBlockQueueCountRemove = 0;
                    bool removed = false;

                    while (!removed && networkBlockQueueCountRemove < 10)
                    {
                        if (Globals.NetworkBlockQueue.ContainsKey(key.Key))
                        {
                            removed = Globals.NetworkBlockQueue.TryRemove(key.Key, out _);
                        }

                        if (!removed)
                        {
                            networkBlockQueueCountRemove++;
                            await Task.Delay(20);
                        }
                    }
                }
                catch { }
            }

            if (Globals.FailedValidators.Count() > 0)
            {
                try
                {
                    var failedValsToRemove = Globals.FailedValidators.Where(x => x.Value < blockHeight).ToList();
                    if (failedValsToRemove?.Count() > 0)
                    {
                        foreach (var key in failedValsToRemove)
                        {
                            var FailedValidatorsCountRemove = 0;
                            bool removed = false;

                            while (!removed && FailedValidatorsCountRemove < 10)
                            {
                                if (Globals.FailedValidators.ContainsKey(key.Key))
                                {
                                    removed = Globals.FailedValidators.TryRemove(key.Key, out _);
                                }

                                if (!removed)
                                {
                                    FailedValidatorsCountRemove++;
                                    await Task.Delay(20);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        public static async Task CleanupProofs()
        {
            var blockHeight = Globals.LastBlock.Height;

            var keysToRemove = Globals.WinningProofs.Where(x => x.Key < blockHeight).ToList();

            var backupKeysToRemove = Globals.BackupProofs.Where(x => x.Key < blockHeight).ToList();

            var networkBlockQueueToRemove = Globals.NetworkBlockQueue.Where(x => x.Key < blockHeight).ToList();

            foreach (var key in keysToRemove)
            {
                try
                {
                    Globals.WinningProofs.TryRemove(key.Key, out _);
                }
                catch { }
            }

            foreach (var key in backupKeysToRemove)
            {
                try
                {
                    Globals.BackupProofs.TryRemove(key.Key, out _);
                }
                catch { }
            }

            foreach(var key in networkBlockQueueToRemove)
            {
                try
                {
                    Globals.NetworkBlockQueue.TryRemove(key.Key, out _);
                }
                catch { }
            }

            if(Globals.FailedValidators.Count() > 0)
            {
                try
                {
                    var failedValsToRemove = Globals.FailedValidators.Where(x => x.Value < blockHeight).ToList();
                    if (failedValsToRemove?.Count() > 0)
                    {
                        foreach (var val in failedValsToRemove)
                        {
                            Globals.FailedValidators.TryRemove(val.Key, out _);
                        }
                    }
                }
                catch { }
            }
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
