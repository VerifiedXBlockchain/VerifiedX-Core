using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VerifiedXCore.Extensions;
using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.EllipticCurve;
using VerifiedXCore.Services;
using LiteDB;

namespace VerifiedXCore.Models
{
    public class Transaction
    {
        public ObjectId Id { get; set; }

        [StringLength(128)]
        public string Hash { get; set; }
        [StringLength(36)]
        public string ToAddress { get; set; }
        [StringLength(36)]
        public string FromAddress { get; set; }
        public decimal Amount { get; set; }
        public long Nonce { get; set; }
        public decimal Fee { get; set; }
        public long Timestamp { get; set; }
        public string? Data { get; set; } = null;
        public long? UnlockTime { get; set; } = null;
        
        [StringLength(512)]
        public string Signature { get; set; }
        public long Height { get; set; }
        public TransactionType TransactionType { get; set; }
        public TransactionRating? TransactionRating { get; set; }
        public TransactionStatus? TransactionStatus { get; set; }

        public void Build()
        {
            Hash = GetHash();
        }
        /// <summary>
        /// The exact string the transaction hash is computed over — plain concatenation, no
        /// separators: Timestamp + FromAddress + ToAddress + Amount + Fee + Nonce + TransactionType
        /// (the enum NAME) + Data, with UnlockTime appended only when set. Amount/Fee concatenate via
        /// decimal.ToString(), so a whole-number amount reads "0.0" and others keep their scale.
        /// Exposed so an external signer can recompute <see cref="GetHash"/> before signing instead
        /// of signing blind; the raw build endpoints return it verbatim.
        /// </summary>
        public string GetHashPreimage()
        {
            return UnlockTime == null ? Timestamp + FromAddress + ToAddress + Amount + Fee + Nonce + TransactionType + Data :
                Timestamp + FromAddress + ToAddress + Amount + Fee + Nonce + TransactionType + Data + UnlockTime;
        }

        /// <summary>
        /// Double SHA-256 of <see cref="GetHashPreimage"/>, where the SECOND pass hashes the
        /// lowercase hex TEXT of the first (not its bytes). Result is lowercase hex.
        /// </summary>
        public string GetHash()
        {
            return HashingService.GenerateHash(HashingService.GenerateHash(GetHashPreimage()));
        }

        /// <summary>Hash for privacy transaction types; see privacy implementation plan.</summary>
        public string BuildPrivate()
        {
            var data = GetPrivateHashInput();
            Hash = HashingService.GenerateHash(HashingService.GenerateHash(data));
            return Hash;
        }

        private string GetPrivateHashInput()
        {
            var sb = new StringBuilder();
            sb.Append(Timestamp);
            sb.Append(TransactionType);
            sb.Append(Data ?? "");
            if (TransactionType == TransactionType.VFX_SHIELD || TransactionType == TransactionType.VBTC_V2_SHIELD)
            {
                sb.Append(FromAddress);
                sb.Append(Amount);
                sb.Append(Nonce);
            }
            return sb.ToString();
        }
        public static void Add(Transaction transaction)
        {
            var transactions = GetAll();
            transactions.InsertSafe(transaction);
        }
        public static LiteDB.ILiteCollection<Transaction> GetAll()
        {
            var trans = DbContext.DB_Wallet.GetCollection<Transaction>(DbContext.RSRV_TRANSACTIONS);
            return trans;
        }
    }
        /// <summary>
        /// Ordinals are PINNED. They are serialized as integers into block JSON, the local DB and
        /// every external consumer's transaction scanner (e.g. VBTC_V2_TRANSFER = 26 and
        /// VBTC_V2_BRIDGE_POOL_UNLOCK = 39 in the exchange integration doc). Add new types at the END
        /// with the next value; never insert in the middle or reorder.
        /// </summary>
    public enum TransactionType
    {
        TX = 0,
        NODE = 1,
        NFT_MINT = 2, //mint
        NFT_TX = 3, //transfer or other process (not for sale or burn)
        NFT_BURN = 4, //burn nft
        NFT_SALE = 5, //sale NFT
        ADNR = 6, //address dnr
        DSTR = 7, //DST shop registration
        VOTE_TOPIC = 8, //voting topic for validators to vote on
        VOTE = 9, //cast vote for topic
        RESERVE = 10, //create a reserve TX
        SC_MINT = 11, //standard sc mint
        SC_TX = 12, //standard sc tx
        SC_BURN = 13, //standard sc burn
        FTKN_MINT = 14, //fungible token mint
        FTKN_TX = 15, //fungible token tx
        FTKN_BURN = 16, //fungible token burn
        TKNZ_MINT = 17, //tokenization token mint
        TKNZ_TX = 18, //tokenization token tx
        TKNZ_BURN = 19, //tokenization token burn
        TKNZ_WD_ARB = 20,
        TKNZ_WD_OWNER = 21,
        VBTC_V2_VALIDATOR_REGISTER = 22, // Validator registers for vBTC v2
        VBTC_V2_VALIDATOR_HEARTBEAT = 23, // Validator heartbeat
        VBTC_V2_VALIDATOR_EXIT = 24, // Validator exits vBTC v2 pool
        VBTC_V2_CONTRACT_CREATE = 25, // Create vBTC v2 contract
        VBTC_V2_TRANSFER = 26, // Transfer vBTC v2 tokens
        VBTC_V2_WITHDRAWAL_REQUEST = 27, // Request withdrawal to BTC
        VBTC_V2_WITHDRAWAL_COMPLETE = 28, // Complete withdrawal
        VBTC_V2_WITHDRAWAL_CANCEL = 29, // Request cancellation
        VBTC_V2_WITHDRAWAL_VOTE = 30, // Validator votes on cancellation
        VFX_SHIELD = 31,
        VFX_UNSHIELD = 32,
        VFX_PRIVATE_TRANSFER = 33,
        VBTC_V2_SHIELD = 34,
        VBTC_V2_UNSHIELD = 35,
        VBTC_V2_PRIVATE_TRANSFER = 36,
        VBTC_V2_BRIDGE_LOCK = 37, // Lock vBTC for bridging to Base (user broadcasts)
        VBTC_V2_BRIDGE_UNLOCK = 38, // Unlock vBTC after burn on Base (legacy, single-lock exact match)
        VBTC_V2_BRIDGE_POOL_UNLOCK = 39, // Pool-based unlock: credit vBTC from multiple locks FIFO to a destination VFX address
        VBTC_V2_BRIDGE_EXIT_TO_BTC = 40, // Base burnForBTCExit → record BTC withdrawal intent on VFX
        VBTC_V2_BRIDGE_EXIT_TO_BTC_COMPLETE = 41, // After BTC broadcast / completion
        VBTC_V2_BRIDGE_EXIT_TO_BTC_FAIL = 42, // FROST failed for some locks → blacklist + reverse + retry
    }

    public enum ReserveTransactionType
    {
        Register,
        Callback,
        Recover
    }

    public enum TransactionStatus
    {
        Pending,
        Success,
        Failed,
        Reserved,
        CalledBack,
        Recovered,
        ReplacedByFee,
        Invalid = 999
    }

    public enum TransactionRating
    {
        A = 1,
        B = 2,
        C = 3,
        D = 4,
        E = 5,
        F = 6
    }
}
