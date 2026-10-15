using ImageMagick;
using Microsoft.AspNetCore.SignalR;
using VerifiedXCore.Bitcoin.ElectrumX;
using VerifiedXCore.Bitcoin.Models;
using VerifiedXCore.Data;
using VerifiedXCore.DST;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Models;
using VerifiedXCore.Models.DST;
using VerifiedXCore.Models.SmartContracts;
using VerifiedXCore.Utilities;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security;
using System.Collections.Generic;

namespace VerifiedXCore
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

        public const int    ValidatorRequiredRBX = 5_000;
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
        /// <summary>
        /// vBTC withdrawal-gate consensus fixes activation height. At/after this height:
        /// (a) legacy mined withdrawal requests with RequestBlockHeight==0 (pre-upgrade rows) stop
        ///     blocking their contract after a grace window instead of blocking forever;
        /// (b) the consensus gate ignores local-only rows (TransactionHash==""), which exist on a
        ///     single API node and were a fork vector;
        /// (c) a per-requestor repeat-offense cooldown applies after an expired-incomplete request.
        /// Inert (rule never active) until set. Testnet/mainnet values assigned in Program/WardenService.
        /// </summary>
        public static long V2WithdrawalExpiryFixHeight = 7_281_000L;
        /// <summary>
        /// vBTC owner add-back consensus fix activation height. At/after this height the owner
        /// balance add-back (VBTCWithdrawalRequest.GetCompletedWithdrawalAmount) counts ALL
        /// completed withdrawals on the contract instead of only the owner's own: every completed
        /// withdrawal shrank the shared BTC deposit pot, but only the requestor got a burn row, so
        /// an owner-only add-back understated the owner by the sum of non-owner completed
        /// withdrawals. Permissive-only (owner balances can only rise), so historical replay is
        /// safe; the gate exists for the mixed-fleet upgrade window (the legacy TransferVBTCV2()
        /// validator site enforces the owner formula at block acceptance with no crafter-trust
        /// bypass). Inert (legacy owner-only behavior) until set. Testnet/mainnet values assigned
        /// in Program/WardenService.
        /// </summary>
        public static long V2WithdrawalOwnerAddBackFixHeight = 7_281_000L;
        /// <summary>
        /// Activation height for the legacy TransferVBTCV2() dispatcher's blockVerify bypass.
        /// At/after this height an owner-balance shortfall in that path is bypassed during block
        /// verification (trust the block crafter's ElectrumX check), matching VBTC_V2_TRANSFER,
        /// VBTC_V2_WITHDRAWAL_REQUEST, and the bridge-lock path. Before it, the legacy path
        /// hard-enforced the owner formula at block acceptance with ElectrumX skipped — the one
        /// site where divergent local withdrawal stores or a mixed-version fleet could reject
        /// otherwise-valid blocks. Permissive-only. Inert until set; values assigned in
        /// Program/WardenService.
        /// </summary>
        public static long VbtcLegacyTransferBypassFixHeight = 7_281_000L;
        /// <summary>
        /// vBTC V2 multi-contract transfer activation height. At/after this height a
        /// VBTC_V2_TRANSFER whose Data.Function == "TransferVBTCMultiV2()" is validated and
        /// applied as one transaction debiting N contracts (Inputs array) to a single recipient.
        /// Before it, ALL nodes (old and new) ignore Function and treat the TX by its single-shape
        /// top-level fields — a multi-shaped Data has no ContractUID, so pre-gate/old nodes
        /// deterministically reject it ("ContractUID cannot be null"). The gate must guard BOTH
        /// the validator branch and the StateData dispatch: gating only one side would let a
        /// hybrid TX (multi Function + valid single fields) validate one way and apply the other
        /// during the mixed-fleet window. Inert until set. Testnet/mainnet values assigned in
        /// Program/WardenService.
        /// </summary>
        public static long V2TransferMultiHeight = 7_281_000L;

        /// <summary>
        /// Bridge burn-binding hardening (security fix): at/after this height, bridge unlock /
        /// pool-unlock / exit-to-BTC TXs require caster votes from the committee governing the block
        /// height that also bind the burned amount + destination, the Base receipt must carry the
        /// matching burn event on the bridge contract, burn hashes are single-use, and allocations
        /// are validated against lock state. Before it, legacy (unbound) verification applies so
        /// historical blocks stay valid.
        /// </summary>
        public static long BridgeBurnBindingHeight = 7_281_000L;

        /// <summary>
        /// Bridge intra-block guard (security fix): at/after this height, a block may not contain two
        /// bridge transactions that redeem the same Base burn or draw on the same lock, and
        /// pool-unlock apply consumes the burn and debits each lock BEFORE crediting. Before it, the
        /// legacy behaviour applies so historical blocks stay valid.
        /// </summary>
        public static long BridgeIntraBlockGuardHeight = 7_296_200L;

        /// <summary>
        /// NEW-26: vBTC V2 deposit-address binding. At/after this height a contract creation carrying a TokenizationV2
        /// feature must name the Taproot address of its FROST group key as its DepositAddress and carry validator
        /// attestations of the DKG (FrostDkgAttestation). Earlier contracts stay as recorded (creator-trusted; disclosed).
        /// Inert until set at rollout (mainnet and testnet), after the validators run the attesting build.
        /// </summary>
        public static long VbtcV2DkgAttestationHeight = 7_400_500L;

        /// <summary>
        /// NEW-28: legacy V1 vBTC (arbiter tokenization) is retired. At/after this height no V1 contract is created, no V1
        /// transfer or withdrawal is accepted, and nothing may touch an existing V1 contract - balances are frozen as they
        /// stand (owner decision: holders were told to withdraw beforehand). Same activation as the rest of this upgrade.
        /// </summary>
        public static long VbtcV1RetirementHeight = 7_400_500L;

        /// <summary>
        /// Withdrawal escrow (security fix): at/after this height a vBTC withdrawal REQUEST debits the
        /// requester's ledger immediately (escrow); COMPLETE only finalizes (no second burn) and an
        /// approved cancellation refunds. Before it, the legacy burn-at-completion applies. Without
        /// escrow a holder could receive the BTC, never complete, keep the vBTC and repeat after the
        /// request expired — draining a shared contract's vault.
        /// </summary>
        public static long WithdrawalEscrowHeight = 7_296_200L;

        /// <summary>
        /// vBTC withdrawal concurrency (S3C §0 revision). At/after this height:
        /// (a) a withdrawal request no longer locks its contract: the per-contract active-request gate, the requester
        ///     repeat cooldown and the one-request-per-contract-per-block guard stop applying. Any number of holders - and
        ///     one holder many times - may have requests open on one contract; each is escrowed at REQUEST, and
        ///     SameBlockDebitGuard keeps pending and same-block requests within the holder's balance;
        /// (b) every request must leave a relayable payout after the smallest Bitcoin fee it can pay
        ///     (VBTCService.GetWithdrawalFeeFloorError), both request shapes, per input.
        /// Off-chain, the same height switches validators from one outstanding signed transaction per contract to any
        /// number whose coins do not overlap (FrostWithdrawalSigningTracker). Must be at or after WithdrawalEscrowHeight:
        /// concurrent withdrawals are only safe when every request is escrowed. Inert until set.
        /// </summary>
        public static long VbtcWithdrawalConcurrencyHeight = 7_400_500L;

        /// <summary>
        /// vBTC V2 withdrawal-cancellation voting rules (public and S3C contracts alike; see
        /// Bitcoin.Services.VBTCCancellationVoting). For cancel and vote transactions at/after this height:
        /// (a) voters and the approval denominator are the contract's voter set that is ACTIVE at the block before the
        ///     vote (was: the full DKG snapshot, dead validators included, so a contract with over a quarter of its
        ///     validators gone could never refund escrow), read from committed blocks so replay gives the same answer;
        /// (b) a reject vote that names the Bitcoin transaction its validator signed for the withdrawal rejects the
        ///     cancellation outright: a payable transaction exists, and refunding the escrow would pay twice;
        /// (c) a cancellation is decided (approved or rejected) or lapses after VOTE_WINDOW_BLOCKS; votes on a lapsed or
        ///     decided cancellation are refused;
        /// (d) a withdrawal may be cancelled again once its previous cancellation was rejected or lapsed.
        /// Before it, the legacy rules apply unchanged. Must be above the tip when EVERY node runs a build with these
        /// rules: a cancel mined at/after it is recorded differently by an older build. Testnet value in Program.cs.
        /// </summary>
        public static long VbtcCancellationVoteRulesHeight = 7_400_500L;

        /// <summary>
        /// vBTC V2 vault transaction size. Since NEW-26 a vault's contract body carries every ceremony participant three
        /// times (snapshot, proof participant list, one signed attestation each): about 460 bytes of transaction per
        /// validator, so the 30 KB transaction cap admits a vault only up to ~60 validators (mainnet has 160+). At/after
        /// this height a VBTC_V2_CONTRACT_CREATE, and a Transfer() that resends a vault's stored code unchanged, may be up
        /// to MaxVbtcVaultTxSizeBytes; every other transaction keeps the 30 KB cap. A relaxation checked in block
        /// validation: a block carrying a larger vault transaction is refused by an older build. Testnet value in Program.cs.
        /// </summary>
        public static long VbtcVaultTxSizeHeight = 7_410_000L;
        /// <summary>Covers FrostDkgAttestation.MaxParticipants (512 participants is ~221 KB).</summary>
        public const int MaxVbtcVaultTxSizeBytes = 256 * 1024;

        // ── Fund-loss audit (Oct 2026) ─────────────────────────────────────────────────────────────────────────
        // One release, one fork point: every consensus rule from the Oct 8 2026 fund-loss audit activates at the same
        // mainnet height. Each rule keeps its own constant (so one can be moved alone if ever needed); all are set to
        // FundLossAuditHeight. Must be above the tip when every validator runs this build. Testnet values in Program.cs.
        /// <summary>
        /// Shared mainnet activation height of the fund-loss audit rules. Reset 2026-10-13: the first value (7,472,000,
        /// set 2026-10-08) passed before the build was on the network. Tip 7,496,705 on 2026-10-13 plus three days
        /// (~6,300 blocks/day over the last five). The same height as <see cref="BitcoinAttestationRecordHeight"/>.
        /// </summary>
        public const long FundLossAuditHeight = 7_517_000L;
        /// <summary>
        /// Testnet activation of the same rules (Program.cs / WardenService). Reset 2026-10-13 from 1,099,400: tip
        /// 1,124,010 on 2026-10-13 plus three days (~6,300 blocks/day).
        /// </summary>
        public const long FundLossAuditTestnetHeight = 1_143_500L;

        /// <summary>
        /// Item 1 (stage 1): a ZK-authorized private transaction (VFX/vBTC unshield, private transfer) may not take more
        /// than its shielded pool holds, must carry at least one nullifier and a Merkle root, and an unshield's outer
        /// recipient and amount must equal the ones inside its (hashed) payload. Before this height no proof was ever
        /// required or attached, so a forged unshield minted VFX from nothing (used on mainnet 2026-09-08: 100,033 VFX
        /// against 4.5 ever shielded). Stage 2 (real PLONK proofs required and verified) follows under its own height.
        /// </summary>
        public static long PrivateTxSupplyRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// Item 1 (stage 1): what the supply floor adds back to a pool's recorded TotalShieldedSupply before judging a
        /// transaction. The four forged VFX unshields of 2026-09-08 (blocks 7,255,776 to 7,257,080) subtracted
        /// 2 + 2 + 29 + 100,000 VFX plus four fixed fees of 0.000003 from the VFX pool counter, which now reads about
        /// -100,029 while honest holders' notes (about 4.1 VFX) are unspent in the tree. Without this the floor would
        /// refuse every honest unshield until that much VFX was shielded again. The counter itself is left as the chain
        /// wrote it (no state write, nothing to replay or rebuild); only the judgement is corrected. Mainnet only: the
        /// testnet pool was never forged against (Program.cs clears it there).
        /// </summary>
        public static Dictionary<string, decimal> ShieldedSupplyCorrections = new(StringComparer.Ordinal)
        {
            ["VFX"] = 100_033.000012M,
        };

        /// <summary>
        /// Item 2: Sale_Complete / M_Sale_Complete naming a contract with no state record is refused (every sale check sat
        /// inside "if the record exists" with no else, so the apply credited the inner payment and debited the signer
        /// against no contract: VFX from nothing, or a negative inner amount draining any address), the inner-payment
        /// positivity rule runs before the record lookup, Evolve()/Devolve() on a missing contract are refused, and the
        /// sale apply is a no-op without a record. No such transaction exists on mainnet (history scan 2026-10-08).
        /// </summary>
        public static long SaleCompleteContractRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// Item 3: a reserve Recover() must carry RecoveryAddress, RecoverySigScript and SignatureTime (every recovery
        /// check sat under "if both strings are non-empty", and a missing SignatureTime threw into an empty catch, so the
        /// vault key alone swept a reserve at once to any address named in the transaction), a malformed RESERVE
        /// transaction is refused instead of swallowed, and the apply sends the sweep to the recovery address recorded in
        /// state, not the one carried. All 5 mainnet recoveries carried a signature (history scan 2026-10-08).
        /// </summary>
        public static long ReserveRecoverRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// Item 4: vault-ness is immutable in both directions. NEW-26 freezes a stored vault's code; this adds the inverse:
        /// a contract that is not a vault cannot be given a TokenizationV2 feature by Update(), Transfer(), Evolve(),
        /// Devolve() or ChangeEvolveStateSpecific() (the DKG attestation runs on creations only, so an NFT owner could
        /// rewrite a plain NFT into a vault carrying any deposit address and FROST group key - including a victim
        /// vault's). No such rewrite exists on mainnet (history scan 2026-10-08: 30 body-changing rewrites, none adds
        /// TokenizationV2).
        /// </summary>
        public static long VaultRewriteRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// A vBTC V2 vault is never burned, and a vault's UID is never created twice (LedgerIntegrityRules.VaultNotBurned,
        /// VaultUidIsNew). Burn() deletes the contract's state record, and for a vault that record is the ledger: every
        /// holder's balance and every debit the owner ever made. The tables beside it survive (completed withdrawals,
        /// bridge locks, attested coins), and the creation proof could be sent again, so an owner could burn its vault
        /// and create it again with an empty ledger: the holders' vBTC gone, and its own balance restored to the whole
        /// deposit plus everything it had already withdrawn. Found by the Oct 12 2026 path audit. For a vault the chain
        /// watches the rule needs no height (such a vault exists only under these rules); for every other vault it
        /// starts here.
        /// </summary>
        public static long VaultBurnRuleHeight = FundLossAuditHeight;

        /// <summary>
        /// The rules found by the second path audit (14 October 2026) that refuse something older builds accepted
        /// start here: a contract call must be readable by the rules exactly as the dispatcher reads it; a reserve
        /// account's delayed moves are judged again when they fall due; a withdrawal's completion names a request
        /// that exists on its own contract; and the others listed in docs/IMPL-vBTC-Bitcoin-Attestation.md, section I.
        /// The same fork point as the rest.
        /// </summary>
        public static long PathAuditRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// From this height no transaction's ToAddress may be a registered name (".vfx" or ".rbx"): validation resolved
        /// the name through the node's own name table and accepted it, but every apply credits or assigns the ToAddress as
        /// written, so the VFX, token, NFT or name went to the literal string and nobody could use it, and the verdict
        /// depended on the local table. PathAuditRulesHeight already refuses it for VFX sends and unshields; this covers
        /// every other type. Not active yet.
        /// </summary>
        public static long RecipientIsAddressHeight = 999_999_999_999;

        /// <summary>
        /// The validator stake is bonded from this height (Services.ValidatorBond; owner decision 2026-10-13): a
        /// registered validator cannot send its 5,000 VFX until 1,200 blocks after its exit is mined, and counts for a
        /// vault's key ceremony only once that stake has been locked for two days. Validators active at this height
        /// are bonded at it. A new refusal and a change to who may take part in a key ceremony, so it must be above
        /// the tip when every validator runs this build. One block before the fork point of the rest
        /// (BitcoinAttestationRecordHeight): the first Bitcoin report signer set is taken at that block, and the
        /// stakes it counts are then already locked.
        /// </summary>
        public static long ValidatorBondHeight = FundLossAuditHeight - 1;

        /// <summary>
        /// A validator already active at this height counts as aged when the bonds start (if it holds its stake then).
        /// A height fixed in the release and below the tip when the release is cut, so nobody can register for it once
        /// the bond is known. Mainnet value here; testnet in Program.cs. Unlike the other heights it is NOT moved
        /// forward at the mainnet release: the bond has been public since this branch was first published (October
        /// 2026), and a later height would count everyone who registered after that as aged.
        /// </summary>
        public static long ValidatorBondGrandfatherHeight = 7_496_000L;
        /// <summary>
        /// This node has sent its validator's exit and must not announce itself again: the heartbeat loop re-announces a
        /// validator the registry shows as inactive, which would undo the exit and, under the bond, resume the lock.
        /// Cleared when validating is started again.
        /// </summary>
        public static bool ValidatorExitSent = false;

        /// <summary>
        /// Item 5: the "manual" bid-signature bypass is honoured only for M_Sale_Start() (the validator set it for that
        /// function but tested the string, so a plain Sale_Start() carrying "manual" skipped the bid signature and named
        /// any address as the buyer; that address's own wallet then auto-signed the completion from its balance). The
        /// wallet-side guard - auto-complete only against a bid this wallet placed - is ungated. No Sale_Start on
        /// mainnet carries "manual" (history scan 2026-10-08).
        /// </summary>
        public static long SaleStartBidRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// Item 6: a vBTC withdrawal REQUEST may not reuse a (requester, UniqueId, contract) key under which a request was
        /// already mined. The holder chooses UniqueId, nothing in consensus read it, and the row store's save matched on
        /// that key and kept the old row's Amount and destination: a dust request with an old UniqueId reopened a
        /// completed withdrawal at its old amount (cancel refunded it; FROST would pay it again) and erased the old
        /// Completed row's owner add-back. From this height the request is refused and a mined row is never overwritten
        /// by a later request. No reuse exists on mainnet (history scan 2026-10-08: 17 requests).
        /// </summary>
        public static long WithdrawalUniqueIdRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// Item 9: a contract's Royalty feature (present or not, amount, pay-to) cannot be changed by Update(),
        /// Transfer(), Evolve(), Devolve() or ChangeEvolveStateSpecific(). Evolve()/Devolve() check only not-locked,
        /// sender == minter and recipient == owner and then overwrite the body, and the next sale reads the royalty
        /// from the current body, so the original minter could rewrite a sold NFT to a 99.99% royalty to itself whenever
        /// it was unlocked. No such rewrite exists on mainnet (history scan 2026-10-08).
        /// </summary>
        public static long RoyaltyRewriteRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// Item 10 (owner decision 2026-10-08: refuse rather than build the deferral): a reserve (xRBX) account cannot
        /// send or burn fungible tokens. FTKN_TX was whitelisted for reserves and got a Pending row and unlock time, but
        /// the token apply moved the balance at once and neither CallBack() nor Recover() touches token accounts, so a
        /// stolen vault key could empty a reserve's tokens with no 24h window and no recovery. No FTKN_TX from a reserve
        /// exists on mainnet (history scan 2026-10-08).
        /// </summary>
        public static long ReserveTokenRulesHeight = FundLossAuditHeight;

        /// <summary>
        /// Item 1 (burn, owner decision 2026-10-08): at exactly this block, before its transactions apply, each address in
        /// ForgedVfxBurns loses the VFX it received from nothing - the four forged unshields of 2026-09-08 paid
        /// 100,033 VFX plus fees to one address, which still holds about 100,028 (5 went to a reserve registration).
        /// The burn takes min(balance, listed amount), so the balance never goes below zero and a re-apply of the block
        /// after a state restore burns the same amount once. Nothing is credited anywhere: the supply shrinks.
        /// Same height as the freeze (shared FundLossAuditHeight); mainnet only (Program.cs clears it on testnet).
        /// </summary>
        public static long ForgedVfxBurnHeight = FundLossAuditHeight;

        /// <summary>
        /// Item 1 (stage 2): the proof-rules epoch of the shielded pool (PrivacyEpoch). At exactly this block every
        /// shielded pool is reset and the circuit-compatible tree starts (leaf 0 = the public dummy note); no private
        /// transaction is valid in that block. From the next block a ZK-authorized private transaction must carry a PLONK
        /// proof that verifies against VFXPI1 version-2 public inputs rebuilt from its own fields, and a node whose native
        /// library cannot verify (no VXPLNK06 params, or a library without the v2 owner-bound circuits) refuses it (fail closed).
        /// Notes shielded before the height can never be proven and are abandoned (owner decision: test funds, ~4.1 VFX).
        /// Sentinel 999,999,999,999 = inert; set at the stage-2 release, above the tip when every validator runs it.
        /// </summary>
        public static long PrivateTxProofRulesHeight = 999_999_999_999L;
        public static Dictionary<string, decimal> ForgedVfxBurns = new(StringComparer.Ordinal)
        {
            ["RB3eeBH258arkuePCJf5NMVMSaUyhyy9g6"] = 100_033.000012M,
        };

        /// <summary>
        /// Item 1 (freeze, owner decision 2026-10-08): from this height no transaction may be sent from an address in
        /// FrozenAddresses. The one entry received 100,033 VFX through the forged unshields of 2026-09-08 (blocks up to
        /// 7,257,080) and still holds about 100,028; whether to burn that balance is a separate decision. Receiving stays
        /// open. Checked at admission (tip + 1) and in block validation (the block's height), so replay below the height
        /// is unchanged.
        /// </summary>
        public static long AddressFreezeHeight = FundLossAuditHeight;

        // ── Bitcoin facts in consensus (docs/PLAN-vBTC-Bitcoin-Attestation.md). Heights and checkpoints set 2026-10-13
        //    (the mainnet values are set again when the release goes to mainnet). ──────────────────────────────────────

        /// <summary>
        /// H0, observation: validators watch Bitcoin blocks and build reports locally; nothing is sent. Needs no
        /// coordination, so it is set at the tip of 2026-10-13: a validator starts watching as soon as it runs this
        /// build, which leaves the days before H1 to compare Bitcoin tips between validators. Mainnet value; testnet in
        /// Program.cs (<see cref="BitcoinAttestationObserveTestnetHeight"/>).
        /// </summary>
        public static long BitcoinAttestationObserveHeight = 7_496_000L;
        /// <summary>The three attestation heights on testnet (applied in Program.cs and WardenService): the tip of 2026-10-13, tip plus three days, and three days after that (plan 8.4).</summary>
        public const long BitcoinAttestationObserveTestnetHeight = 1_124_000L;
        public const long BitcoinAttestationRecordTestnetHeight = FundLossAuditTestnetHeight;
        public const long BitcoinAttestationTestnetHeight = 1_163_000L;
        /// <summary>
        /// H1, recording: the Bitcoin report and rewind transactions are accepted and applied, and a vault created from
        /// this height on is watched: its deposit script is in state, its owner's balance is its attested coins, and its
        /// withdrawals and bridge exits follow pin / settle / void (with the requester's fee bump). Vaults created before
        /// this height keep the earlier rules until H2. The new types are a hard fork for a node that does not know
        /// them, so this must be above the height at which every validator and caster runs this release. It is also the
        /// height from which a peer must be on this major version (<see cref="MinimumPeerMajorVersion"/>). Set 2026-10-13
        /// to the fund-loss audit height (tip plus three days): one release, one fork point.
        /// </summary>
        public static long BitcoinAttestationRecordHeight = FundLossAuditHeight;
        /// <summary>
        /// H2, enforcement: vaults created before H1 are frozen (nothing debits a balance on them, and no bridge exit or
        /// unlock draws on their locks), and the transactions only the earlier way of working needs (withdrawal
        /// completion, cancellation vote, bridge exit completion) are refused for every vault. The time between H1 and
        /// H2 is for moving to a new vault: two weeks on mainnet, three days on testnet (plan 8.4). Set 2026-10-13:
        /// H1 plus two weeks at ~6,400 blocks/day.
        /// </summary>
        public static long BitcoinAttestationHeight = 7_607_000L;

        /// <summary>The major version peers were required to run before this release.</summary>
        public const int PriorMajorVer = 8;

        /// <summary>
        /// The lowest major version a peer may run at <paramref name="height"/>. A node on <see cref="MajorVer"/> used to
        /// refuse every peer on a lower major version at once, which cut a newly upgraded node off from a network still
        /// on the previous release and made a one-node-at-a-time upgrade impossible. The two releases agree on every
        /// rule until H1 (<see cref="BitcoinAttestationRecordHeight"/>), so until then a peer on the previous major
        /// version is accepted; from H1 a node that has not upgraded cannot follow the chain and is refused.
        /// </summary>
        public static int MinimumPeerMajorVersion(long height) =>
            height >= Math.Min(BitcoinAttestationRecordHeight, ValidatorBondHeight) ? MajorVer : Math.Min(MajorVer, PriorMajorVer); // the bonds start one block before H1: the two releases part there

        /// <summary>
        /// Where this chain's view of Bitcoin starts on mainnet (plan section 8.1). Height of the LAST of the twelve
        /// checkpoint headers; it must be the first block of a difficulty interval (divisible by 2016). 0 = not set.
        /// </summary>
        public static long BitcoinCheckpointHeightMainnet = 969_696;
        /// <summary>
        /// The twelve consecutive Bitcoin mainnet headers ending at <see cref="BitcoinCheckpointHeightMainnet"/>, 80 bytes
        /// each in hex, oldest first, joined (whitespace and commas between them are ignored). Paste from any block
        /// explorer at release time; the node checks that they link and that each meets its own proof-of-work.
        /// </summary>
        /// Set 2026-10-13: blocks 969,685 to 969,696 (the last is 000000000000000000001e39df127cbab82a824ac43cfcdbf62e59f6a08b9f0b),
        /// read from mempool.space and checked against blockstream.info.
        public static string BitcoinCheckpointHeadersMainnet =
            "0080072018ca59a6dd2b6c0e46eb8acc7c6611b57321e5e0a86e010000000000000000004bea33bb33d1fdad8654b03f4004197eb6719c9f2cf7b7bb77bd3b529c96932a5a9cc06ac51e0217a560ec5e" +
            "004066219a8c00261cf0e47cb6e04c184202657330567bcdaed10100000000000000000020ff2db82f76d9fcea70a32806a47904803eb88ea51794caddc65feea6264383b8a0c06ac51e02178b4b1801" +
            "00e03325e3fe2966c165fe7c388c6f34f513b98afacc166a82cb01000000000000000000fe87c792df2d94d9ef3e97f747ed44197deb702189f0501700619a69e06e2ccbf8a1c06ac51e021779fa4b53" +
            "002000209999b8e41f68031ed9a3c5ed83b925676ee79b4c70c700000000000000000000c166b96edf8fee93cf1c642d4207df4dfd20b0cffac06d4cfd271e31e058009d20a5c06ac51e021701aa44ba" +
            "00c04724221f4345dd6fd6789765c108bd5eb6c6e5529be030ea01000000000000000000fb92a2327e4b1ade75cd2710fad18b27282631c21d5960bba3c79db9322865ebaca5c06ac51e02170718cec4" +
            "00200220e4677da8d41b97e25289487916a88d24f7047b1524ec0100000000000000000044a722d5cbd3bce2ec04e4c5c0c22a14d7e59a87b6fba21ff2a4836420f649518da6c06ac51e02176821e4df" +
            "00409d25c4e4f05f88b995ad34e15448671843c6dfbcc579745d000000000000000000005fa8eefb29ee6c0c0d42b9122c2d676d94e955300c4fe7da20d57e47c451489992a7c06ac51e0217769c8c23" +
            "00600a202fc787a2cd24f77224d9ece2e08f43156ff7e84201ac000000000000000000004fe5696d61bb207b0093a4f207a7be5af81fafcfdcf0769f67792ed132ff999cd9a8c06ac51e0217045b3479" +
            "00200520a3bdb538cf1c2d9a9cabc1d929fc6eedc87cd71e4ea6000000000000000000003c26a0f61d2c4070c41f4fd5813748ee836bb7c265e76f605092c5b5b6d478ee14abc06ac51e02178c205ba2" +
            "0020082039fa7abdc6f212da89c3a260148f33c60644b71b61710000000000000000000042359bc9481c2d090a7ec11d036b19df3eb0c852313bbc855c2305bd53cdff2a50abc06ac51e021700f0e9ae" +
            "00000038c8423e19b206b534b9ddfb365c407b6b05cc85f6301b000000000000000000003234e2a63c1c7d747fb8076515d0f46a8f1403b6f292291fa30d9e66380ad07e88abc06ac51e0217183e320e" +
            "00e00020ea7409145abed3693b6aa5e269dea055406e0962804301000000000000000000a8f40cc468f3aa4518fdc368e37fc7aff01eb12b293885f847517602a96e3a93d4abc06af01e021779d3a409";
        /// <summary>As <see cref="BitcoinCheckpointHeightMainnet"/>, for Bitcoin testnet4 (the VFX testnet).</summary>
        public static long BitcoinCheckpointHeightTestnet = 155_232;
        /// <summary>As <see cref="BitcoinCheckpointHeadersMainnet"/>, for Bitcoin testnet4.</summary>
        /// Set 2026-10-13: testnet4 blocks 155,221 to 155,232 (the last is 0000000000000002314dc5b90175408b22d2c80dfe99979629b40e572d40345b),
        /// read from mempool.space and checked against two other explorers.
        public static string BitcoinCheckpointHeadersTestnet =
            "00e00422464783a8ec0371af02e41bb89392469b2f6a65200583b20254fbcc0000000000d832ee5d0c0dcbb6f499c5fcbcd499139d8843b5df506aaa0bea03a0b621abcd7687c36affff001d2801efd9" +
            "0000002041ae6a0e6fe359b11225bae36bf2c210a02e56c5ea26e76ba0b88d0000000000d3669166e10d36c7e658abce1cc92b28f1560d5b2dafcdf34cf1160567e48454278cc36affff001d521685b1" +
            "00000020e4a00ad2bb9c5b9f35f6c7b5e65b5ea1f762845b2abc0b42a9b389ae0000000030589a3a2c971f7a60ab872b1be254358d68e4df6d429be7930b156d4eec9c7bd890c36affff001d121abfab" +
            "000000200f3846f43fc05f736ef711f077a84e0904e9efd1a5c747619d07dc0c00000000fa4993dca019ec9e1cfe6640d035886085b3bbe459643416b4dc806eafe530638995c36affff001d5e0ddf82" +
            "0000002042f469d300162601f3b66b19c9915ad375fd061812c3799ebc9d5c0000000000e7a35434b8bf5a818f5c481005ac912d772d0dcfa97dd0a7fe66a8372d8cd0133a9ac36affff001dabb8bc2f" +
            "000000209a801d1145f0d940659d1b9508a2bf5efb77a5f61876c697fde2d7520000000023f24e1b201f144cb51a1d4f2f71df7e82849440501d7ca42667ea46190a135feb9ec36affff001d119a4cab" +
            "00c0fd29be213abfa1ac7bdcdb675bb814f6851fccd4e500fe9adbdf3e771e80000000008649813182029ed592553819b46a475d769640f537239a90aed932daca8c29247787c36a6e3f031938fb3930" +
            "000056209e1c2b12ed97d68554b079472eecc622ed2418fa257d5b35000000000000000043b4ff5885eefeabaaaa4f6bad4749cd2db4b9ab6e0c1ec220f542fe6f1d4391288cc36affff001db8004c4c" +
            "0040bf21aa63f10433621af301e2040935c393ad6429ca1fafea8076b854d800000000007646430c9d1d20eb71e253b454ecdca1bfb8ff3f20e5ac0412c9e21d23ca4ae6d990c36affff001d4b18987f" +
            "00205622c641616194c97c09763dec871693c7e668f8f5889bfbb0907fbdaa0000000000e6aff20aa01019f5f19c1f39cb548eb5ef270885017e0e427ea995e9bcdaf74b8a95c36affff001d6c391966" +
            "0040dd21c2f8139c10fec685a51b67b3c5b85a798c5a161234776e042ac28b00000000005514adc04431860a2c882aaf0decd5ea573999052e193077c495028162439fe03b9ac36affff001d6e00c924" +
            "00c01133a4b225a064922585bd9db58ad1cb7127031c8ba15252c0db7a31cc00000000007f6bf7535d4eb0882eb99466ccacdc6f6848c61d4936aec2e67c75e233f7c363e397c36a437e0319e11e4279";
        public static readonly HashSet<string> FrozenAddresses = new(StringComparer.Ordinal)
        {
            "RB3eeBH258arkuePCJf5NMVMSaUyhyy9g6",
        };

        /// <summary>
        /// Base (vBTC.b) minting validator set = registered vBTC validators that are ALSO members of
        /// the caster committee. vBTC validator registration is a free self-transaction gated only
        /// by a wallet balance, so mirroring the raw registry onto the Base contract would let a
        /// Sybil reach the 2/3 mint threshold. The caster committee is the accountable set.
        /// </summary>
        public static bool BaseValidatorSetCastersOnly = true;
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
        /// <summary>VX-18: highest Bitcoin fee rate (sat/vB) any local signing path will use. Config "MaxBtcFeeRateSatPerVb".</summary>
        public static long MaxBtcFeeRateSatPerVb = 2_000;
        public static int ChainCheckPointInterval = 0;
        public static int ChainCheckPointRetain = 0;
        public static int PasswordClearTime = 10;
        public static int NFTTimeout = 0;
        public static int Port = 3338;
        public static int BeaconPort = 23338;
        public static int BeaconWebPort = 23339;
        //deprecate in v5.0.1 or greater
        public static int ADJPort = 3339;
        public static int ValPort = 3339;
        public static int SelfSTUNPort = 3340;
        public static int DSTClientPort = 3341;
        public static int ArbiterPort = 3342;
        public static int FrostValidatorPort = 7295;

        // S3C (Self-Custodial Smart Contracts)
        public static List<Bitcoin.Models.S3CEntry>? S3CPool = null;   // minting-node side (parsed from S3C= config)
        public static bool UseS3C => S3CPool != null && S3CPool.Count >= 3;
        public static bool IsS3CValidator = false;                     // validator side (S3CValidator=true)
        public static bool S3CConfigInvalid = false;                   // S3C= present but failed validation → refuse mints (§2.1)
        public static int APIPort = 7292;
        public static int ValAPIPort = 7294;
        public static int APIPortSSL = 7777;
        public static int MajorVer = 9;
        public static int MinorVer = 0;
        public static int RevisionVer = 0;
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
        public static bool FrostFFIAvailable = false;
        public static string FrostFFIVersion = "Unknown";
        public static string FrostFFICheckSummary = "";
        public static bool PortsOpened = false;

        /// <summary>Ethereum/Base address derived from this node's validator private key (empty if not a validator).</summary>
        public static string ValidatorBaseAddress { get; set; } = string.Empty;

        /// <summary>VBTCb proxy contract on Base (set from config). When set, bridge paths apply.</summary>
        public static string VBTCbContractAddress { get; set; } = string.Empty;

        /// <summary>When true, vBTC V2 tokens use only the built-in default logo; custom images
        /// and beacon upload/download are stepped over (code retained). Set false to restore.</summary>
        public static bool VBTCDefaultAssetOnly = true;

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

        /// <summary>Phase E: operator disaster-recovery override — lets a LONE seed bootstrap without
        /// 2-of-3 agreement. Default false; set ForceSoloBootstrap=true in config.txt only when the
        /// other seeds are unrecoverable. Bypassing agreement risks a fork if other seeds are alive.</summary>
        public static bool ForceSoloBootstrap = false;

        /// <summary>Wave 2 (/depart): set when a graceful departure is in progress — the casting
        /// loop stops entering rounds immediately and the node winds down toward shutdown.</summary>
        public static bool IsDeparting = false;

        /// <summary>Wave 5: state-driven producer readiness. False while this node is recovering
        /// (resync, fork recovery/reorg, snapshot restore, state probation, or unsynced) so
        /// casters exclude it from winner selection — no timers, clears itself when recovery
        /// completes. Round-retry + shared exclusion remain the backstop for mid-round failures.</summary>
        public static bool ProducerReady =>
            !IsResyncing
            && IsChainSynced
            && !IsDeparting
            && !Utilities.ForkRecoveryUtility.IsRecoveryInProgress
            && !Utilities.SnapshotRestoreUtility.IsRestoreRunning
            && !Utilities.SnapshotRestoreUtility.StateDestroyedByFailedRestore
            && !Services.BlockValidatorService.GetProbationStatus().Active
            // STALL-RESOLVE: a node whose tip has not moved while the network is ahead is
            // stranded on a dead branch — casters must not select it as a winner.
            && !Services.ForkDetectionService.IsStalled;

        /// <summary>Legacy proofs, GET block fallback, optional cert skip, and seed peer injection apply only for seed casters when the tip looks stopped. Other nodes always use normal snapshot/signed paths and discovery.
        /// Phase E: additionally requires a signed ≥2-of-3 seed agreement (<see cref="Services.BootstrapCoordinationService.AgreementActive"/>) — bootstrap is never entered unilaterally.</summary>
        public static bool IsBootstrapMode => IsLocalBootstrapCaster && IsChainStalledForBootstrap && Services.BootstrapCoordinationService.AgreementActive;

        /// <summary>Blocks with Height &gt;= this require a valid <see cref="Models.Block.ConsensusCertificate"/>.
        /// Wave 6: armed automatically from the genesis membership record's EffectiveFromHeight
        /// (CasterMembershipStore.TryArmFromGenesis) — NOT hand-edited. long.MaxValue until a
        /// genesis record is minted at the coordinated restart, so historical blocks stay valid.</summary>
        public static long CertEnforceHeight = long.MaxValue;

        /// <summary>Strict consensus-compatibility version exchanged in the validator/blockcaster handshakes.
        /// Bump on ANY consensus-behavior change; peers with a different (or missing) value are refused
        /// consensus connections. Changes only with a coordinated network release.</summary>
        public static int ConsensusVersion = 1;

        // Wave 6: the restart boundary is no longer a compile-time constant. It is the genesis
        // membership record's EffectiveFromHeight — minted by the seeds at the coordinated
        // restart's bootstrap agreement and adopted by every node via the signed record chain.
        // CertEnforceHeight (above) is armed from it by CasterMembershipStore.TryArmFromGenesis.
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
                // 6/5-OVERFLOW FIX: self-heal duplicate entries (same validator address added
                // twice, e.g. under two IP spellings) by rebuilding the bag with one entry per
                // address. Keep is deterministic (lowest normalized IP) so every node that holds
                // the same duplicated list heals to the same result. Distinct casters beyond
                // MaxBlockCasters are NOT trimmed here — only logged — since trimming a live
                // caster on some nodes but not others would diverge quorum views.
                var casterSnapshot = BlockCasters.Where(p => !string.IsNullOrEmpty(p.ValidatorAddress)).ToList();
                if (casterSnapshot.GroupBy(p => p.ValidatorAddress).Any(g => g.Count() > 1))
                {
                    var deduped = casterSnapshot
                        .GroupBy(p => p.ValidatorAddress)
                        .Select(g => g.OrderBy(p => (p.PeerIP ?? "").Replace("::ffff:", ""), StringComparer.Ordinal).First())
                        .ToList();
                    var addressless = BlockCasters.Where(p => string.IsNullOrEmpty(p.ValidatorAddress)).ToList();
                    BlockCasters = new ConcurrentBag<Peers>(deduped.Concat(addressless));
                    Utilities.CasterLogUtility.Log(
                        $"SyncKnownCastersFromBlockCasters: removed {casterSnapshot.Count - deduped.Count} duplicate caster entr(ies). Count now {deduped.Count}",
                        "CasterFlow");
                    casterSnapshot = deduped;
                }
                if (casterSnapshot.Count > MaxBlockCasters)
                {
                    Utilities.CasterLogUtility.Log(
                        $"SyncKnownCastersFromBlockCasters WARNING — caster count {casterSnapshot.Count} exceeds MaxBlockCasters {MaxBlockCasters}. addrs=[{string.Join(",", casterSnapshot.Select(p => p.ValidatorAddress))}]",
                        "CasterFlow");
                }

                KnownCasters.Clear();
                foreach (var p in casterSnapshot)
                {
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
        /// <summary>HUB-SPLIT (Sep 2026): the /blockcaster hub's own connection registry. It used to share
        /// <see cref="P2PValDict"/> with the /validator hub, keyed by IP only — every caster is also a
        /// validator, so whichever hub connected second aborted the first, and the loser's disconnect
        /// handler then erased the winner's entry. A mutual-eviction loop: ~12,500 consecutive caster
        /// handshake failures over a week on testnet, invisible because rejections logged nothing.</summary>
        public static ConcurrentDictionary<string, HubCallerContext> P2PCasterDict = new ConcurrentDictionary<string, HubCallerContext>();
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
        // NEW-27: the built-in entries (70e34dd1..., 9065618f..., b05b230c...) were added in 2023 as a testnet bypass; none of
        // them is in mainnet (scanned to 4,589,149) or the current testnet, and they let any content carrying one of those
        // hashes skip validation. Operator entries (console / bad-TX prompt) are added at runtime; see
        // LedgerIntegrityRules.IsHonoredWhitelistEntry.
        public static List<string> BadNFTTxList = new List<string>();
        public static List<string> BadTopicTxList = new List<string>();
        public static List<string> BadVoteTxList = new List<string>();
        public static List<string> BadTxList = new List<string>(); // NEW-27: see BadNFTTxList
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
