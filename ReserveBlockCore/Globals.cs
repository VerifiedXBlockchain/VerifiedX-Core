using ImageMagick;
using Microsoft.AspNetCore.SignalR;
using ReserveBlockCore.Bitcoin.ElectrumX;
using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.DST;
using ReserveBlockCore.EllipticCurve;
using ReserveBlockCore.Models;
using ReserveBlockCore.Models.DST;
using ReserveBlockCore.Models.SmartContracts;
using ReserveBlockCore.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security;
using System.Collections.Generic;

namespace ReserveBlockCore
{
    public static partial class Globals
    {
        static Globals()
        {
            var Source = new CancellationTokenSource();
            Source.Cancel();
            CancelledToken = Source.Token;

            if (MaxBlockCasters < 3)
                MaxBlockCasters = 3;
            else if (MaxBlockCasters > 5)
                MaxBlockCasters = 5;
        }

        public class MethodCallCount
        {
            public int Enters { get; set; }
            public int Exits { get; set; }
            public int Exceptions { get; set; }
        }

        public class SwaggerResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; }
        }


        #region Timers
        public static bool IsTestNet = false;
        public static bool IsCustomTestNet = false;
        public static string CustomTestNetName = "";

        public static string LeadAddress = "RBXpH37qVvNwzLjtcZiwEnb3aPNG815TUY";      
        public static Timer? ValidatorListTimer;//checks currents peers and old peers and will request others to try. 
        public static Timer? DBCommitTimer;//checks dbs and commits log files. 
        public static Timer? ConnectionHistoryTimer;//process connections and history of them
        public static Timer? ValidatorRegistryCleanupTimer;//HAL-11 Fix: cleans up stale pending validators and reconciles registry

        #endregion

        #region Global General Variables
        public static byte AddressPrefix = 0x3C; //address prefix 'R'        
        public static ConcurrentDictionary<string, AdjNodeInfo> AdjNodes = new ConcurrentDictionary<string, AdjNodeInfo>(); // IP Address        
        public static ConcurrentDictionary<string, bool> Signers = new ConcurrentDictionary<string, bool>();
        public static ConcurrentDictionary<string, bool> RetiredSigners = new ConcurrentDictionary<string, bool>();
        public static ConcurrentDictionary<string, MethodCallCount> MethodDict = new ConcurrentDictionary<string, MethodCallCount>();
        public static ConcurrentDictionary<string, ReserveTransactions> ReserveTransactionsDict = new ConcurrentDictionary<string, ReserveTransactions>();
        public static ConcurrentDictionary<string, string> SeedDict = new ConcurrentDictionary<string, string>();
        public static ConcurrentDictionary<string, TokenDetails> Tokens = new ConcurrentDictionary<string, TokenDetails>();
        public static ConcurrentDictionary<long, string> ProofBlockHashDict = new ConcurrentDictionary<long, string>();
        public static ConcurrentDictionary<string, ReserveAccountUnlockKey> ReserveAccountUnlockKeys = new ConcurrentDictionary<string, ReserveAccountUnlockKey>();
        public static string SignerCache = "";
        public static string IpAddressCache = "";
        public static object SignerCacheLock = new object();
        public static string FortisPoolCache = "";
        public static Block LastBlock = new Block { Height = -1 };
        public static Block NextValidatorBlock = new Block { Height = -1 };
        public static Adjudicators? LeadAdjudicator = null;
        public static Guid AdjudicatorKey = Adjudicators.AdjudicatorData.GetAdjudicatorKey();
        public static BeaconReference BeaconReference = new BeaconReference();
        public static Beacons? SelfBeacon = null;
        public static long LastBlockAddedTimestamp = TimeUtil.GetTime();
        public static long BlockTimeDiff = 0;
        public static Block? LastWonBlock = null;
        public static Process GUIProcess;
        public static bool IsFork = false;
        public static bool ElectrumXConnected = false;
        public static DateTime ElectrumXLastCommunication = DateTime.Now;
        public static bool BTCAccountCheckRunning = false;
        public static Blockchain Blockchain { get; set; }
        public static List<ClientSettings> ClientSettings { get; set; }
        public static NBitcoin.Network BTCNetwork { get; set; }
        public static string SegwitP2SHStartPrefix { get; set; }
        public static string SegwitTaprootStartPrefix { get; set; }
        public static Bitcoin.Bitcoin.BitcoinAddressFormat BitcoinAddressFormat { get; set; }
        public static Account ArbiterSigningAddress { get; set; }
        public static NBitcoin.ScriptPubKeyType ScriptPubKeyType { get; set; }
        public static DateTime BTCAccountLastCheckedDate = DateTime.Now;
        public static DateTime LastRanBTCReset = DateTime.Now.AddMinutes(-5);
        public static decimal BTCMinimumAmount = 0.00001M;
        public static bool BTCSyncing = false;  

        public static DateTime? RemoteCraftLockTime = null;        
        public static DateTime? CLIWalletUnlockTime = null;
        public static DateTime? APIUnlockTime = null;
        public static DateTime? ExplorerValDataLastSend = null;


        public const int ValidatorRequiredRBX = 50_000;
        public const decimal ADNRRequiredRBX = 5.0M;
        public const decimal ADNRTransferRequiredRBX = 1.0M;
        public const decimal ADNRDeleteRequiredRBX = 0.0M;
        public const decimal TopicRequiredRBX = 10.0M;
        public const decimal DecShopRequiredRBX = 10.0M;
        public const decimal DecShopUpdateRequiredRBX = 1.0M;
        public const decimal DecShopDeleteRequiredRBX = 1.0M; //0
        public const decimal RSRVAccountRegisterRBX = 4.0M;
        public static bool HeadlessMode = false;

        public const int ADNRLimit = 65;
        public static int BlockLock = 1079488;
        public static long V3Height = 579015;
        public static long V4Height = 0;
        public static long V1ValHeight = 832000;
        public static long V2ValHeight = 999999999999;
        public static long SpecialBlockHeight = 999999999999;
        public static long TXHeightRule1 = 820457; //March 31th, 2023 at 03:44 UTC
        public static long TXHeightRule2 = 847847; //around April 7, 2023 at 18:30 UTC
        public static long TXHeightRule3 = 1079488; //around June 13th, 2023 at 19:30 UTC
        public static long TXHeightRule4 = 999999999999; //HAL-067: Nonce validation activation height
        public static long TXHeightRule5 = 5436312; //vBTC Lead signer changes
        public static int BlockTime = 12000; //12 seconds
        public static int BlockTimeMin = 10000; //10 seconds
        public static int BlockTimeMax = 15000; //15 seconds

        // HAL-068 Fix: Transaction timestamp validation thresholds
        public const int MaxTxAgeSeconds = 3600; // 60 minutes - transactions older than this are rejected
        public const int MaxFutureSkewSeconds = 120; // 2 minutes - allows for clock skew tolerance

        // HAL-071 Fix: Mempool resource limits to prevent unbounded growth
        public const int MaxMempoolEntries = 10000; // Maximum number of transactions in mempool
        public const long MaxMempoolSizeBytes = 52428800; // 50 MB maximum mempool size
        public const decimal MinFeePerKB = 0.000003M; // Minimum fee per kilobyte to prevent spam
        public const int MempoolCleanupIntervalMinutes = 5; // How often to run mempool cleanup

        //public static long Validating
        public static long LastAdjudicateTime = 0;
        public static SemaphoreSlim BlocksDownloadSlim = new SemaphoreSlim(1, 1);
        public static SemaphoreSlim BlocksDownloadV2Slim = new SemaphoreSlim(1, 1);
        public static int WalletUnlockTime = 0;
        public static int ChainCheckPointInterval = 0;
        public static int ChainCheckPointRetain = 0;
        public static int PasswordClearTime = 10;
        public static int NFTTimeout = 0;
        public static int Port = 3338;
        //deprecate in v5.0.1 or greater
        public static int ADJPort = 3339;
        public static int ValPort = 3339;
        public static int SelfSTUNPort = 3340;
        public static int DSTClientPort = 3341;
        public static int ArbiterPort = 3342;
        public static int FrostValidatorPort = 7295;
        public static int APIPort = 7292;
        public static int ValAPIPort = 7294;
        public static int APIPortSSL = 7777;
        public static int MajorVer = 6;
        public static int MinorVer = 0;
        public static int RevisionVer = 12;
        public static int BuildVer = 0;
        public static int SCVersion = 1;
        public static int ValidatorIssueCount = 0;
        public static bool ValidatorSending = true;
        public static bool ValidatorReceiving = true;
        public static bool ValidatorBalanceGood = true;
        public static List<string> ValidatorErrorMessages = new List<string>();
        public static long ValidatorLastBlockHeight = 0;
        public static string GitHubVersion = $"beta{MajorVer}.{MinorVer}.{RevisionVer}";
        public static string GitHubApiURL = "https://api.github.com/";
        public static string GitHubRBXRepoURL = "repos/VerifiedXBlockchain/VerifiedX-Core/releases/latest";
        public static string GitHubLatestReleaseVersion = "";
        public static ConcurrentDictionary<string, string> GitHubLatestReleaseAssetsDict = new ConcurrentDictionary<string, string>();
        public static bool UpToDate = true;
        public static string StartArguments = "";
        public static DateTime NewUpdateLastChecked = DateTime.UtcNow.AddHours(-2);
        public static SecureString? APIToken = null;
        public static int TimeSyncDiff = 0;
        public static DateTime TimeSyncLastDate = DateTime.Now;
        public static decimal StartMemory = 0;
        public static decimal CurrentMemory = 0;
        public static decimal ProjectedMemory = 0;
        public static long SystemMemory = 1;
        public static int TotalArbiterParties = 2; //change back to 5 after val fix
        public static int TotalArbiterThreshold = 2; //change back to 3 after val fix


        public static string Platform = "";
        public static string ValidatorAddress = "";
        public static string ValidatorPublicKey = "";
        public static string? WalletPassword = null;
        public static string? APIPassword = null;
        public static string? APICallURL = null;
        public static string ChainCheckpointLocation = "";
        public static string ConfigValidator = "";
        public static string ConfigValidatorName = "";
        public static string GenesisAddress = "RBdwbhyqwJCTnoNe1n7vTXPJqi5HKc6NTH";
        public static string CLIVersion = "";
        public static string? MotherAddress = null;
        public static string? CustomPath = null;
        public static string GenesisValidator = "";
        public static string ArbiterURI = "";
        public static List<Models.Arbiter> Arbiters = new List<Models.Arbiter>();
        public static string? ExplorerValDataLastSendResponseCode = null;

        public static bool Lock = true;
        public static bool AlwaysRequireWalletPassword = false;
        public static bool AlwaysRequireAPIPassword = false;
        public static bool StopConsoleOutput = false;
        public static bool StopValConsoleOutput = false;
        /// <summary>When true, caster diagnostic file log (casterlog.txt) and caster-scoped validator console lines are emitted. Default off; set CasterLog=true in config.txt or pass the casterlog CLI flag.</summary>
        public static bool CasterLogEnabled = false;
        public static int AdjudicateLock = 0;        
        public static Account AdjudicateAccount;
        public static PrivateKey AdjudicatePrivateKey;
        public static bool APICallURLLogging = false;
        public static bool ChainCheckPoint = false;
        public static bool PrintConsoleErrors = false;
        public static bool HDWallet = false;
        public static bool IsWalletEncrypted = false;
        public static bool AutoDownloadNFTAsset = false;
        public static bool IgnoreIncomingNFTs = false;
        public static bool ShowTrilliumOutput = false;
        public static bool ShowTrilliumDiagnosticBag = false;
        public static bool ConnectToMother = false;        
        public static bool InactiveNodeSendLock = false;
        public static bool IsCrafting = false;
        public static bool IsResyncing = false;
        public static bool TestURL = false;
        public static bool StopAllTimers = false;
        public static bool DatabaseCorruptionDetected = false;
        public static bool RemoteCraftLock = false;
        public static bool IsChainSynced = false;
        /// <summary>Height at which the post-sync validator liveness sweep was run. PromoteBlockProducer ignores blocks below this height for new validator additions.</summary>
        public static long ValidatorListSyncedHeight = 0;
        /// <summary>Set to true after the post-sync liveness sweep completes. Gates PromoteBlockProducer and P2P gossip additions.</summary>
        public static bool ValidatorLivenessSweepComplete = false;
        public static bool OptionalLogging = false;
        public static bool AdjPoolCheckLock = false;
        public static bool GUI = false;
        public static bool RunUnsafeCode = false;
        public static bool GUIPasswordNeeded = false;
        public static bool TreisUpdating = false;
        public static bool DuplicateAdjIP = false;
        public static bool DuplicateAdjAddr = false;
        public static bool ExplorerValDataLastSendSuccess = false;
        public static bool LogAPI = false;
        public static bool RefuseToCallSeed = false;
        public static bool OpenAPI = false;
        public static bool NFTFilesReadyEPN = false; // nft files ready, encryption password needed
        public static bool NFTsDownloading = false;
        public static bool TimeInSync = true;
        public static bool TimeSyncError = false;
        public static bool BasicCLI = false;
        public static bool ShowSTUNMessagesInConsole = false;
        public static bool STUNServerRunning = false;
        public static bool MemoryOverload = false;
        public static bool SelfSTUNServer = false;
        public static bool LogMemory = false;
        public static bool BlockSeedCalls = false;
        public static bool UseV2BlockDownload = false;
        public static bool IsArbiter = false;
        public static bool IsBlockCaster = false;
        /// <summary>Environment.TickCount64 when a block was last committed (see BlockchainData.AddBlock). Used for caster stall detection.</summary>
        public static long LastBlockProducedTick { get; set; } = 0;
        public static bool IsWardenMonitoring = false;
        public static bool IsFrostValidator = false;
        public static bool IsValidatorPortOpen = false;
        public static bool IsValidatorAPIPortOpen = false;
        public static bool IsFROSTAPIPortOpen = false;
        public static bool PortsOpened = false;

        /// <summary>Ethereum/Base address derived from this node's validator private key (empty if not a validator).</summary>
        public static string ValidatorBaseAddress { get; set; } = string.Empty;

        /// <summary>VBTCb proxy contract on Base (set from config). When set, bridge paths apply.</summary>
        public static string VBTCbContractAddress { get; set; } = string.Empty;

        /// <summary>Base (Ethereum) chain id: 84532 Sepolia when testnet, 8453 mainnet.</summary>
        public static long BaseEvmChainId => IsTestNet ? 84532L : 8453L;

        /// <summary>Active block casters (3–5). Used for adaptive majority on bridge exit consensus.</summary>
        public static int ActiveCasterCount { get; set; } = 3;

        // HAL-17 Fix: Configurable timeout values
        public static int SignalRShortTimeoutMs = 2000;
        public static int SignalRLongTimeoutMs = 6000;
        public static int BlockProcessingDelayMs = 2000;
        public static int NetworkOperationTimeoutMs = 1000;
        
        // HAL-19 Fix: DoS protection settings for block validation
        public static int MaxBlockSizeBytes = 10485760; // 10MB default
        public static int BlockValidationTimeoutMs = 5000;
        
        public static CancellationToken CancelledToken;

        public static ConcurrentDictionary<string, long> MemBlocks = new ConcurrentDictionary<string, long>();
        public static ConcurrentDictionary<string, long> MemMutliTransfers = new ConcurrentDictionary<string, long>();
        public static ConcurrentDictionary<long, string> BlockHashes = new ConcurrentDictionary<long, string>();
        public static ConcurrentDictionary<string, NodeInfo> Nodes = new ConcurrentDictionary<string, NodeInfo>(); // IP Address
        public static ConcurrentDictionary<string, AdjBench> AdjBench = new ConcurrentDictionary<string, AdjBench>(); // IP Address:Key
        public static ConcurrentDictionary<string, Validators> InactiveValidators = new ConcurrentDictionary<string, Validators>(); // VFX address        
        //public static ConcurrentDictionary<string, string> Locators = new ConcurrentDictionary<string, string>(); // BeaconUID
        public static ConcurrentDictionary<string, Mother.Kids> MothersKids = new ConcurrentDictionary<string, Mother.Kids>(); //Mothers Children
        public static ConcurrentDictionary<string, HubCallerContext> MothersKidsContext = new ConcurrentDictionary<string, HubCallerContext>(); //Mothers Children
        public static ConcurrentDictionary<string, Beacons> Beacons = new ConcurrentDictionary<string, Beacons>();
        public static ConcurrentBag<string> RejectAssetExtensionTypes = new ConcurrentBag<string>();
        public static ConcurrentDictionary<string, BeaconNodeInfo> Beacon = new ConcurrentDictionary<string, BeaconNodeInfo>();
        public static ConcurrentQueue<int> BlockDiffQueue = new ConcurrentQueue<int>();
        public static ConcurrentDictionary<string, long> ActiveValidatorDict = new ConcurrentDictionary<string, long>();
        public static ConcurrentBag<StunServer> STUNServers = new ConcurrentBag<StunServer>();
        public static ConcurrentDictionary<string, BitcoinValShares> ArbiterValidatorShares = new ConcurrentDictionary<string, BitcoinValShares>();
        public static ConcurrentDictionary<string, (int failCount, long lastFailTime)> FailedBlockProducers = new ConcurrentDictionary<string, (int failCount, long lastFailTime)>();



        public static SecureString EncryptPassword = new SecureString();
        public static SecureString DecryptPassword = new SecureString();
        public static SecureString? MotherPassword = null;
        public static SecureString ArbiterEncryptPassword = new SecureString();

        public static IHttpClientFactory HttpClientFactory;

        public static List<string> ABL = new List<string>();

        public static DateTime ValidatorStartDate = DateTime.UtcNow;

        public static ConcurrentDictionary<string, long> FailedValidators = new ConcurrentDictionary<string, long>();
        public static int ElmahFileStore = 10000;
        public static string ReportedIP = "";
        public static bool ReportedIPManuallySet = false;

        #endregion

        #region P2P Client Variables

        public const int MaxPeers = 10;
        public const int MaxValPeers = 20;
        public const int MaxBlockCasterPeers = 4;
        public static int MaxBlockCasters = 5;

        /// <summary>How old the tip must be (seconds) before seed casters treat the chain as stopped and use bootstrap-only paths.</summary>
        public const int BootstrapChainStallThresholdSeconds = 120;

        /// <summary>Hardcoded seed caster addresses (mainnet + testnet). Only these nodes may enter <see cref="IsBootstrapMode"/> when the tip is stale.</summary>
        public static readonly HashSet<string> BootstrapCasterAddresses = new HashSet<string>(StringComparer.Ordinal)
        {
            "RK28ywrBfEXV5EuARn3etyVXMtcmywNxnM",
            "RFoKrASMr19mg8S71Lf1F2suzxahG5Yj4N",
            "RH9XAP3omXvk7P6Xe9fQ1C6nZQ1adJw2ZG",
            "xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj",
            "xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC",
            "xCkUC4rrh2AnfNf78D5Ps83pMywk5vrwpi",
        };

        /// <summary>True if this node’s validating address is one of the seed casters.</summary>
        public static bool IsLocalBootstrapCaster =>
            !string.IsNullOrEmpty(ValidatorAddress) && BootstrapCasterAddresses.Contains(ValidatorAddress);

        /// <summary>No tip yet → treat as stopped (cold start). After <see cref="IsChainSynced"/>, tip older than <see cref="BootstrapChainStallThresholdSeconds"/> vs wall clock → network likely stopped (~10+ missed slots at 12s target).</summary>
        public static bool IsChainStalledForBootstrap
        {
            get
            {
                if (LastBlock == null || LastBlock.Height < 0)
                    return true;
                if (!IsChainSynced)
                    return false;
                var now = TimeUtil.GetTime();
                return now - LastBlock.Timestamp > BootstrapChainStallThresholdSeconds;
            }
        }

        /// <summary>Legacy proofs, GET block fallback, optional cert skip, and seed peer injection apply only for seed casters when the tip looks stopped. Other nodes always use normal snapshot/signed paths and discovery.</summary>
        public static bool IsBootstrapMode => IsLocalBootstrapCaster && IsChainStalledForBootstrap;

        /// <summary>Blocks with Height &gt;= this require a valid <see cref="Models.Block.ConsensusCertificate"/> (when not bootstrap). Edit the initializer here only — not loaded from config.txt.</summary>
        public static long CertEnforceHeight = long.MaxValue;
        public static long LastProofBlockheight = 0;
        public static ConcurrentDictionary<string, int> ReportedIPs = new ConcurrentDictionary<string, int>();
        public static ConcurrentDictionary<string, Peers> BannedIPs;
        public static ConcurrentDictionary<string, int> SkipPeers = new ConcurrentDictionary<string, int>();
        public static ConcurrentDictionary<string, int> SkipValPeers = new ConcurrentDictionary<string, int>();
        public static ConcurrentDictionary<string, NetworkValidator> NetworkValidators = new ConcurrentDictionary<string, NetworkValidator>(); //key = vfx address
        /// <summary>FIX E: Unix timestamp until which P2P gossip should NOT re-add new validators.
        /// Set after fresh-startup clears stale NetworkValidators to prevent peers from immediately repopulating them.</summary>
        public static long GossipCooldownUntil = 0;
        public static ConcurrentDictionary<string, Peers> ValidatorPool = new ConcurrentDictionary<string, Peers>();
        public static ConcurrentDictionary<string, NodeInfo> ValidatorNodes = new ConcurrentDictionary<string, NodeInfo>(); //key = ipaddress
        public static ConcurrentDictionary<string, NodeInfo> BlockCasterNodes = new ConcurrentDictionary<string, NodeInfo>(); //key = ipaddress
        public static ConcurrentDictionary<long, Proof> WinningProofs = new ConcurrentDictionary<long, Proof>();
        public static ConcurrentDictionary<long, string> FinalizedWinner = new ConcurrentDictionary<long, string>();
        public static ConcurrentBag<Proof> Proofs = new ConcurrentBag<Proof>();
        public static ConcurrentDictionary<long, Block> NetworkBlockQueue = new ConcurrentDictionary<long, Block>();
        public static ConcurrentDictionary<long, List<Proof>> BackupProofs = new ConcurrentDictionary<long, List<Proof>>();
        public static ConcurrentDictionary<string, DateTime?> ProofsBroadcasted = new ConcurrentDictionary<string, DateTime?>();
        public static ConcurrentDictionary<long, DateTime?> BlockQueueBroadcasted = new ConcurrentDictionary<long, DateTime?>();
        public static ConcurrentDictionary<string, (long, int)> FailedProducerDict = new ConcurrentDictionary<string, (long, int)>();
        public static ConcurrentDictionary<string, int> ProducerDict = new ConcurrentDictionary<string, int>();
        public static ConcurrentQueue<ConsensusHeader> ConsensusHeaderQueue = new ConcurrentQueue<ConsensusHeader>();
        public static ConcurrentBag<string> FailedProducers = new ConcurrentBag<string>();
        public static ConcurrentBag<Peers> BlockCasters = new ConcurrentBag<Peers>();
        public static ConcurrentDictionary<long, string> CasterApprovedBlockHashDict = new ConcurrentDictionary<long, string>();
        public static ConcurrentDictionary<long, CasterRound> CasterRoundDict = new ConcurrentDictionary<long, CasterRound>();
        public static ConcurrentDictionary<long, CasterRoundAudit> CasterRoundAuditDict = new ConcurrentDictionary<long, CasterRoundAudit>();
        public static ConcurrentDictionary<string, Proof> CasterProofDict = new ConcurrentDictionary<string, Proof>();
        /// <summary>Winner vote exchange: key = height, value = dict of (casterIP -> chosen winner address). Used for mandatory winner agreement phase.</summary>
        public static ConcurrentDictionary<long, ConcurrentDictionary<string, string>> CasterWinnerVoteDict = new ConcurrentDictionary<long, ConcurrentDictionary<string, string>>();
        /// <summary>DETERMINISTIC-CONSENSUS: Tracks excluded addresses per voter per height for shared exclusion during winner agreement.</summary>
        public static ConcurrentDictionary<long, ConcurrentDictionary<string, List<string>>> CasterExcludedAddressDict = new ConcurrentDictionary<long, ConcurrentDictionary<string, List<string>>>();
        /// <summary>
        /// CONSENSUS-V2 (Fix #5): Per-height proof-set commitments exchanged between casters.
        /// Outer key = block height; inner key = caster ValidatorAddress; value = the commitment
        /// that caster broadcast for that height. Used by ReachProofSetAgreementAsync to converge
        /// every caster on the same sorted proof-address set before winner sorting.
        /// Cleaned up to height-10 each round.
        /// </summary>
        public static ConcurrentDictionary<long, ConcurrentDictionary<string, ProofSetCommitment>> CasterProofSetCommitDict = new ConcurrentDictionary<long, ConcurrentDictionary<string, ProofSetCommitment>>();

        /// <summary>Discovered / agreed caster set (synced from <see cref="BlockCasters"/> and discovery). Used with <see cref="BlockCasters"/> for certificate verification (plan §Appendix C).</summary>
        public static object KnownCastersLock = new object();
        public static List<CasterInfo> KnownCasters { get; } = new List<CasterInfo>();

        public static void SyncKnownCastersFromBlockCasters()
        {
            lock (KnownCastersLock)
            {
                KnownCasters.Clear();
                foreach (var p in BlockCasters)
                {
                    if (string.IsNullOrEmpty(p.ValidatorAddress))
                        continue;
                    KnownCasters.Add(new CasterInfo
                    {
                        Address = p.ValidatorAddress,
                        PeerIP = (p.PeerIP ?? "").Replace("::ffff:", ""),
                        PublicKey = p.ValidatorPublicKey ?? ""
                    });
                }

                ActiveCasterCount = Math.Max(3, Math.Min(5, KnownCasters.Count > 0 ? KnownCasters.Count : 3));
            }
        }

        #endregion

        #region P2P Server Variables

        public static ConcurrentDictionary<string, HubCallerContext> P2PPeerDict = new ConcurrentDictionary<string, HubCallerContext>();
        public static ConcurrentDictionary<string, HubCallerContext> P2PValDict = new ConcurrentDictionary<string, HubCallerContext>();
        public static ConcurrentDictionary<string, HubCallerContext> BeaconPeerDict = new ConcurrentDictionary<string, HubCallerContext>();        
        public static ConcurrentDictionary<string, MessageLock> MessageLocks = new ConcurrentDictionary<string, MessageLock>();
        public static ConcurrentDictionary<string, int> TxRebroadcastDict = new ConcurrentDictionary<string, int>();

        // HAL-054 Fix: Global resource tracking for distributed DoS protection
        public static int GlobalConnectionCount = 0;
        public static long GlobalBufferCost = 0;
        public const int MaxGlobalConnections = 500; // Maximum concurrent connections across all IPs
        public const long MaxGlobalBufferCost = 100000000; // 100MB total buffer across all IPs
        public const int MaxConnectionsPerIP = 200; // Per-IP connection limit
        public const int MaxBufferCostPerIP = 5000000; // 5MB per-IP buffer limit

        #endregion

        #region P2P Adj Server Variables

        public static ConcurrentMultiDictionary<string, string, FortisPool> FortisPool = new ConcurrentMultiDictionary<string, string, FortisPool>(); // IP address, VFX address        
        public static ConcurrentMultiDictionary<string, string, BeaconPool> BeaconPool = new ConcurrentMultiDictionary<string, string, BeaconPool>(); // IP address, Reference
        public static ConcurrentDictionary<string, ConnectionHistory.ConnectionHistoryQueue> ConnectionHistoryDict = new ConcurrentDictionary<string, ConnectionHistory.ConnectionHistoryQueue>();
        public static ConcurrentBag<ConnectionHistory> ConnectionHistoryList = new ConcurrentBag<ConnectionHistory>();
        public static ConcurrentDictionary<string, long> Signatures = new ConcurrentDictionary<string, long>();
        
        public static TaskWinner CurrentWinner;
        public static string VerifySecret = "";        

        public static ConcurrentDictionary<string, ConcurrentDictionary<string, (DateTime Time, string Request, string Response)>> ConsensusDump = new ConcurrentDictionary<string, ConcurrentDictionary<string, (DateTime Time, string Request, string Response)>>();
        public static long ConsensusStartHeight = -1;
        public static long ConsensusSucceses = 0;
        
        public static ConcurrentDictionary<string, Transaction> BroadcastedTrxDict = new ConcurrentDictionary<string, Transaction>(); // TX Hash
        public static ConcurrentDictionary<string, TransactionBroadcast> ConsensusBroadcastedTrxDict = new ConcurrentDictionary<string, TransactionBroadcast>(); //TX Hash
        public static ConcurrentDictionary<string, DuplicateValidators> DuplicatesBroadcastedDict= new ConcurrentDictionary<string, DuplicateValidators>();
        public static ConcurrentDictionary<string, long> TxLastBroadcastTime = new ConcurrentDictionary<string, long>(); // TX Hash -> Unix timestamp of last broadcast

        #endregion        

        #region Bad TX Ignore List

        public static List<string> BadADNRTxList = new List<string> { "9ebe7eb08abcf35f7e5cad6a5346babcb045f0e52732cdfddd021296331c2056"};
        public static List<string> BadNFTTxList = new List<string>() { "70e34dd1b5d646addc5328f971b4ab370095985dcf4bce1d0e1ea222824daa6d" };
        public static List<string> BadTopicTxList = new List<string>();
        public static List<string> BadVoteTxList = new List<string>();
        public static List<string> BadTxList = new List<string> { "9065618ff356dc1dcef8cd5413ffe826f8ab45ca8b6bb9c8f9853d1de0b576ae", "b05b230c9f7fb6f9014c0a9a4a5b1c9ddaf36a96462635d628272b8c62e2e5b3" };
        public static List<string> BadDSTList = new List<string> { "8f9eec99c69ace2ad758048ceb281c38099173ca95a97c31114f2d136b34916a", 
        "a898112b2770ca2182d330d71f8830ad7eeb2b7ac9030cf33312ebeefd72c8a5",
        "152250f2673234765ab61e3f46e2ef94a80e50cf24bcaaf0ad5e0341f8b5626a",
        "241546578f04a3dbcf9e9195352750f7ff087ba39840759fe38e56e22f9d6139",};

        public static List<string> BadNodeList = new List<string>();

        #endregion

        #region DST Variables
        public const decimal BidModifier = 100000000M;
        public const decimal BidMinimum = 0.00000001M;
        public static ConcurrentDictionary<string, DSTConnection> ConnectedClients = new ConcurrentDictionary<string, DSTConnection>();
        public static ConcurrentDictionary<string, DSTConnection> ConnectedShops = new ConcurrentDictionary<string, DSTConnection>();
        public static DSTConnection? STUNServer = null;
        public static ConcurrentQueue<Message> ClientMessageQueue = new ConcurrentQueue<Message>();
        public static ConcurrentQueue<Message> ServerMessageQueue = new ConcurrentQueue<Message>();
        public static ConcurrentDictionary<string, List<Chat.ChatMessage>> ChatMessageDict = new ConcurrentDictionary<string, List<Chat.ChatMessage>>();
        public static ConcurrentDictionary<string, IPEndPoint> ShopChatUsers = new ConcurrentDictionary<string, IPEndPoint>();
        public static ConcurrentDictionary<string, MessageState> ClientMessageDict = new ConcurrentDictionary<string, MessageState>();
        public static ConcurrentDictionary<string, Message> ServerMessageDict = new ConcurrentDictionary<string, Message>();
        public static DecShopData? DecShopData = null;
        public static ConcurrentDictionary<string, DecShopData> MultiDecShopData = new ConcurrentDictionary<string, DecShopData>();
        public static ConcurrentQueue<BidQueue> BidQueue = new ConcurrentQueue<BidQueue>();
        public static ConcurrentQueue<BidQueue> BuyNowQueue = new ConcurrentQueue<BidQueue>();
        public static ConcurrentDictionary<IPEndPoint, int> AssetAckEndpoint = new ConcurrentDictionary<IPEndPoint, int>();
        public static ConcurrentDictionary<string, (bool, int)> PingResultDict = new ConcurrentDictionary<string, (bool, int)>();
        public static bool AssetDownloadLock = false;
        public static readonly HashSet<string> ValidExtensions = new HashSet<string>()
        {
            "png",
            "jpg",
            "jpeg",
            "jp2",
            "gif",
            "tif",
            "tiff",
            "webp",
            "bmp",
            "pdf"
            // Other possible extensions
        };

        #endregion

    }
}
