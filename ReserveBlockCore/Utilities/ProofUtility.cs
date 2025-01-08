using Newtonsoft.Json;
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

            var badList = Globals.FailedProducers.ToList();

            var newPeers = Globals.NetworkValidators.Values.Where(x => x.CheckFailCount <= 3 && !badList.Contains(x.Address)).ToList();

            List<NetworkValidator> peersMissingDataList = new List<NetworkValidator>();
            List<string> CompletedIPs = new List<string>();
            List<string> CompletedAddresses = new List<string>();

            foreach (var val in newPeers)
            {
                if (val.Address != null && val.PublicKey != null)
                {
                    if (CompletedIPs.Contains(val.IPAddress) ||
                        CompletedAddresses.Contains(val.Address) ||
                        IsProducerExcluded(val.Address) ||
                        badList.Contains(val.Address))
                        continue;

                    CompletedIPs.Add(val.IPAddress);
                    CompletedAddresses.Add(val.Address);

                    var stateAddress = StateData.GetSpecificAccountStateTrei(val.Address);
                    if (stateAddress == null)
                    {
                        continue;
                    }

                    if (stateAddress.Balance < ValidatorService.ValidatorRequiredAmount())
                    {
                        continue;
                    }

                    var proof = await CreateProof(val.Address, val.PublicKey, blockHeight, prevHash);
                    if (proof.Item1 != 0 && !string.IsNullOrEmpty(proof.Item2))
                    {
                        Proof _proof = new Proof
                        {
                            Address = val.Address,
                            BlockHeight = blockHeight,
                            PreviousBlockHash = prevHash,
                            ProofHash = proof.Item2,
                            PublicKey = val.PublicKey,
                            VRFNumber = proof.Item1,
                            IPAddress = val.IPAddress.Replace("::ffff:", "")
                        };

                        proofs.Add(_proof);
                    }
                }
                else
                {
                    //TODO: Try to get info or remove them.
                    peersMissingDataList.Add(val);
                }
            }

            CompletedIPs.Clear();
            CompletedAddresses.Clear();

            CompletedIPs = new List<string>();
            CompletedAddresses = new List<string>();

            Globals.LastProofBlockheight = blockHeight;

            return proofs;
        }
        public static async Task<List<Proof>> GenerateCasterProofs()
        {
            List<Proof> proofs = new List<Proof>();

            var blockHeight = Globals.LastBlock.Height + 1;
            var prevHash = Globals.LastBlock.Hash;

            var peerDB = Peers.GetAll();
            //Force unban quicker
            await BanService.RunUnban();

            var badList = Globals.FailedProducers.ToList();

            var newPeers = Globals.BlockCasters.Where(x => !badList.Contains(x.PeerIP)).ToList();

            List<NetworkValidator> peersMissingDataList = new List<NetworkValidator>();
            List<string> CompletedIPs = new List<string>();
            List<string> CompletedAddresses = new List<string>();

            foreach (var val in newPeers)
            {
                if (val.ValidatorAddress != null && val.ValidatorPublicKey != null)
                {
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
                            IPAddress = val.PeerIP.Replace("::ffff:", "")
                        };

                        proofs.Add(_proof);
                    }
                }
                else
                {
                    //TODO: Try to get info or remove them.
                    //peersMissingDataList.Add(val);
                }
            }

            CompletedIPs.Clear();
            CompletedAddresses.Clear();

            CompletedIPs = new List<string>();
            CompletedAddresses = new List<string>();

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
                    var uri = $"http://{winningProof.IPAddress.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/VerifyBlock/{nextBlock}/{winningProof.ProofHash}";
                    var response = await client.GetAsync(uri).WaitAsync(new TimeSpan(0, 0, 7));

                    if (response != null)
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            //var blockJson = await response.Content.ReadAsStringAsync();
                            //if (blockJson == null)
                            //    return (false, null);

                            //var block = JsonConvert.DeserializeObject<Block>(blockJson);

                            //if (block == null)
                            //    return (false, null);

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
                    var uri = $"http://{ip.Replace("::ffff:", "")}:{Globals.ValPort}/valapi/validator/VerifyBlock/{nextBlock}/aaa";
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
                    p.VerifyProof() &&
                    !Globals.ABL.Exists(x => x == p.Address)
                ).ToList();

                // Sort deterministically by VRF number
                return validProofs
                    .OrderBy(x => x.VRFNumber)  // Closest to zero wins
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
