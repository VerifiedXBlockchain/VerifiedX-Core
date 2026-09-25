using System.Collections.Generic;
using Newtonsoft.Json;
using VerifiedXCore;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Services;
using Xunit;

namespace VerifiedXCore.Tests
{
    /// <summary>
    /// NEW-16 (found by the fifth independent review; CRITICAL, pre-existing): coinbase transactions skip VerifyTX and
    /// every per-type rule, but UpdateTreis applied their type and Data. A winning producer set its coinbase to an
    /// FTKN_TX TokenTransfer() of a victim's tokens to itself (reviewer PoC: victim 1000 -> 0, validator +1000).
    /// </summary>
    public class NEW16_CoinbaseShapeTests
    {
        private static Block BlockWith(Transaction coinbase)
        {
            coinbase.Build(); // producers build coinbases (NEW-25 binds content to hash)
            return new Block
            {
            Height = 10_000, Version = 4, Validator = "xValidator",
                Transactions = new List<Transaction> { coinbase },
            };
        }

        [Fact]
        public void NEW16_PoC_CoinbaseCarryingATokenTransfer_Refused()
        {
            var coinbase = new Transaction
            {
                FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.FTKN_TX,
                Data = JsonConvert.SerializeObject(new { Function = "TokenTransfer()", ContractUID = "c:1", FromAddress = "xVictim", ToAddress = "xValidator", Amount = 1000M }),
            };
            Assert.False(BlockchainData.ValidateBlock(BlockWith(coinbase)));
        }

        [Fact]
        public void NEW16_PoC_CoinbaseWithDataOnATxType_Refused()
        {
            var coinbase = new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.TX, Data = "{\"Function\":\"Transfer()\"}" };
            Assert.False(BlockchainData.ValidateBlock(BlockWith(coinbase)));
        }

        [Fact]
        public void NEW16_Control_PlainCoinbase_Accepted()
        {
            var coinbase = new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.TX };
            Assert.True(BlockchainData.ValidateBlock(BlockWith(coinbase)));
        }

        // ── NEW-25 (sixth review): coinbase content bound to its hash ───────────────────────

        [Fact]
        public void NEW25_OrdinaryBlock_EveryValueFieldOfTheCoinbaseIsPinned()
        {
            // A peer serving blocks may change a coinbase while keeping its Hash (and so the merkle root and block hash).
            // On an ordinary block every value-bearing field is fixed by the checks, without recomputing the hash.
            Transaction Fresh() => new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.TX, Timestamp = 1 };
            Assert.True(BlockchainData.ValidateBlock(BlockWith(Fresh())));                                   // control
            var changes = new System.Action<Transaction>[]
            {
                t => t.Amount = 50_000_000M, t => t.ToAddress = "xAttacker", t => t.Fee = 1M, t => t.Nonce = 7,
                t => t.UnlockTime = 99, t => t.Data = "x", t => t.TransactionType = TransactionType.FTKN_TX,
            };
            foreach (var change in changes)
            {
                var coinbase = Fresh();
                var block = BlockWith(coinbase);
                change(coinbase);                                                                            // Hash kept
                Assert.False(BlockchainData.ValidateBlock(block));
            }
        }

        [Fact]
        public void NEW25_Correction_MainnetCoinbaseWhoseStoredScaleDiffers_Accepted()
        {
            // Mainnet block 811,860 as stored: Fee 0 (scale 0) although the producer hashed Fee 0.00, so the content no
            // longer recomputes to the stored hash - true of 2,262,322 mainnet coinbases. NEW-25 as first committed
            // refused them, which stopped a mainnet sync from genesis at block 399,792.
            var coinbase = new Transaction
            {
                FromAddress = "Coinbase_BlkRwd", ToAddress = "RYDsPQZk72R7Rf48s158otsydZ9bR4o7bi", Amount = 32.00M, Fee = 0M, Nonce = 0,
                TransactionType = TransactionType.TX, Timestamp = 1680024504, Height = 811_860,
                Hash = "5b953fdd52164419dcc319f293e8904fd90336ba7627d52250ddc95f78031a54",
            };
            Assert.NotEqual(coinbase.Hash, coinbase.GetHash()); // precondition: the stored content does not recompute
            var block = new Block { Height = 811_860, Version = 3, Validator = "RYDsPQZk72R7Rf48s158otsydZ9bR4o7bi", Transactions = new List<Transaction> { coinbase } };
            var prior = Globals.LastBlock;
            try
            {
                Globals.LastBlock = new Block { Height = 811_859 }; // block reward 32
                Assert.True(BlockchainData.ValidateBlock(block));
            }
            finally { Globals.LastBlock = prior; }
        }

        [Fact]
        public void NEW25_SpecialBlockHeightNoLongerSkipsTheCoinbaseChecks()
        {
            var prior = Globals.SpecialBlockHeight;
            try
            {
                Globals.SpecialBlockHeight = 10_000;
                var coinbase = new Transaction { FromAddress = "Coinbase_BlkRwd", ToAddress = "xValidator", Amount = 0M, TransactionType = TransactionType.TX, Timestamp = 1 };
                var block = BlockWith(coinbase);
                coinbase.Amount = 50_000_000M; // Hash kept
                Assert.False(BlockchainData.ValidateBlock(block));
            }
            finally { Globals.SpecialBlockHeight = prior; }
        }
    }
}
