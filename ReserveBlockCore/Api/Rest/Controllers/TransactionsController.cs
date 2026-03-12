using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.Extensions;
using ReserveBlockCore.Models;
using ReserveBlockCore.P2P;
using ReserveBlockCore.Services;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class TransactionsController : RestBaseController
    {
        /// <summary>
        /// Send a raw transaction
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SendRaw([FromBody] object jsonData)
        {
            var txJToken = JToken.Parse(jsonData.ToString()!);
            var dataTest = txJToken["Data"] != null ? txJToken["Data"]!.ToString(Formatting.None) : null;
            txJToken["Data"] = dataTest;
            var transaction = JsonConvert.DeserializeObject<Transaction>(txJToken.ToString());

            if (transaction == null)
                return Fail("DESERIALIZE_FAILED", "Failed to deserialize transaction.");

            transaction.ToAddress = transaction.ToAddress.ToAddressNormalize();
            transaction.Amount = transaction.Amount.ToNormalizeDecimal();

            var twSkipVerify = transaction.TransactionType == TransactionType.TKNZ_WD_OWNER;
            var result = !twSkipVerify
                ? await TransactionValidatorService.VerifyTX(transaction)
                : await TransactionValidatorService.VerifyTX(transaction, false, false, true);

            if (!result.Item1)
                return Fail("VERIFICATION_FAILED", "Transaction was not verified.");

            if (transaction.TransactionRating == null)
            {
                var rating = await TransactionRatingService.GetTransactionRating(transaction);
                transaction.TransactionRating = rating;
            }

            await TransactionData.AddToPool(transaction);
            await P2PClient.SendTXMempool(transaction);

            return Created(new { Hash = transaction.Hash });
        }

        /// <summary>
        /// Simple send (from, to, amount in body)
        /// </summary>
        [HttpPost("send")]
        public async Task<IActionResult> Send([FromBody] SendTransactionRequest request)
        {
            var addrCheck = AddressValidateUtility.ValidateAddress(request.ToAddress);
            if (!addrCheck)
                return Fail("INVALID_ADDRESS", "This is not a valid VFX address.");

            var result = await WalletService.SendTXOut(request.FromAddress, request.ToAddress, request.Amount);

            if (result == "FAIL" || result.Contains("FAIL"))
                return Fail("TX_FAILED", result);

            return Created(new { Result = result });
        }

        /// <summary>
        /// List local transactions (filterable by status, paginated)
        /// </summary>
        [HttpGet]
        public IActionResult GetAll([FromQuery] string? status, [FromQuery] PaginationParams paging)
        {
            IEnumerable<Transaction> txList = status?.ToLower() switch
            {
                "pending" => TransactionData.GetLocalPendingTransactions(),
                "failed" => TransactionData.GetLocalFailedTransactions(),
                "success" => TransactionData.GetSuccessfulLocalTransactions(),
                "mined" => TransactionData.GetLocalMinedTransactions(),
                _ => TransactionData.GetAllLocalTransactions(true)
            };

            var allTxs = txList.ToList();
            var totalCount = allTxs.Count;

            var paged = allTxs
                .Skip((paging.Page - 1) * paging.PageSize)
                .Take(paging.PageSize);

            return OkPaged(paged, paging.Page, paging.PageSize, totalCount);
        }

        /// <summary>
        /// Get transaction by hash (local)
        /// </summary>
        [HttpGet("{hash}")]
        public IActionResult GetByHash(string hash)
        {
            var tx = TransactionData.GetTxByHash(hash);
            if (tx == null)
                return Fail("NOT_FOUND", $"Transaction not found: {hash}", 404);

            return Ok(tx);
        }

        /// <summary>
        /// Search full chain for a transaction by hash
        /// </summary>
        [HttpGet("search/{hash}")]
        public IActionResult SearchNetwork(string hash)
        {
            var coreCount = Environment.ProcessorCount;
            if (coreCount < 4 && !Globals.RunUnsafeCode)
                return Fail("INSUFFICIENT_CORES", "System does not have enough cores for chain search. Enable RunUnsafeCode in config.", 400);

            hash = hash.Replace(" ", "");
            var blocks = BlockchainData.GetBlocks();
            var height = Convert.ToInt32(Globals.LastBlock.Height);
            string? foundTx = null;

            Parallel.ForEach(
                Enumerable.Range(0, height + 1).Reverse(),
                new ParallelOptions { MaxDegreeOfParallelism = coreCount <= 4 ? 2 : 4 },
                (blockHeight, loopState) =>
                {
                    var block = blocks.Query().Where(x => x.Height == blockHeight).FirstOrDefault();
                    if (block != null)
                    {
                        var result = block.Transactions.FirstOrDefault(x => x.Hash == hash);
                        if (result != null)
                        {
                            foundTx = JsonConvert.SerializeObject(result);
                            loopState.Break();
                        }
                    }
                });

            if (foundTx == null)
                return Fail("NOT_FOUND", "No transaction found with that hash.", 404);

            var tx = JsonConvert.DeserializeObject<Transaction>(foundTx);
            return Ok(tx);
        }

        /// <summary>
        /// Verify a raw transaction
        /// </summary>
        [HttpPost("verify")]
        public async Task<IActionResult> Verify([FromBody] object jsonData)
        {
            var txJToken = JToken.Parse(jsonData.ToString()!);
            var dataTest = txJToken["Data"] != null ? txJToken["Data"]!.ToString(Formatting.None) : null;
            txJToken["Data"] = dataTest;
            var transaction = JsonConvert.DeserializeObject<Transaction>(txJToken.ToString());

            if (transaction == null)
                return Fail("DESERIALIZE_FAILED", "Failed to deserialize transaction.");

            transaction.ToAddress = transaction.ToAddress.ToAddressNormalize();
            transaction.Amount = transaction.Amount.ToNormalizeDecimal();

            var twSkipVerify = transaction.TransactionType == TransactionType.TKNZ_WD_OWNER;
            var result = !twSkipVerify
                ? await TransactionValidatorService.VerifyTX(transaction)
                : await TransactionValidatorService.VerifyTX(transaction, false, false, true);

            if (!result.Item1)
                return Fail("VERIFICATION_FAILED", $"Transaction was not verified. Error: {result.Item2}");

            return Ok(new { Hash = transaction.Hash, Verified = true });
        }

        /// <summary>
        /// Estimate transaction fee
        /// </summary>
        [HttpPost("fee")]
        public IActionResult EstimateFee([FromBody] object jsonData)
        {
            var txJToken = JToken.Parse(jsonData.ToString()!);
            var dataTest = txJToken["Data"] != null ? txJToken["Data"]!.ToString(Formatting.None) : null;
            txJToken["Data"] = dataTest;
            var tx = JsonConvert.DeserializeObject<Transaction>(txJToken.ToString());

            if (tx == null)
                return Fail("DESERIALIZE_FAILED", "Failed to deserialize transaction.");

            var nTx = new Transaction
            {
                Timestamp = tx.Timestamp,
                FromAddress = tx.FromAddress,
                ToAddress = tx.ToAddress.ToAddressNormalize(),
                Amount = tx.Amount + 0.0M,
                Fee = 0,
                Nonce = AccountStateTrei.GetNextNonce(tx.FromAddress),
                TransactionType = tx.TransactionType,
                Data = tx.Data
            };

            nTx.Fee = FeeCalcService.CalculateTXFee(nTx);

            return Ok(new { Fee = nTx.Fee });
        }

        /// <summary>
        /// Calculate transaction hash
        /// </summary>
        [HttpPost("hash")]
        public IActionResult CalculateHash([FromBody] object jsonData)
        {
            var txJToken = JToken.Parse(jsonData.ToString()!);
            var dataTest = txJToken["Data"] != null ? txJToken["Data"]!.ToString(Formatting.None) : null;
            txJToken["Data"] = dataTest;
            var tx = JsonConvert.DeserializeObject<Transaction>(txJToken.ToString());

            if (tx == null)
                return Fail("DESERIALIZE_FAILED", "Failed to deserialize transaction.");

            tx.ToAddress = tx.ToAddress.ToAddressNormalize();
            tx.Amount = tx.Amount.ToNormalizeDecimal();
            tx.Build();

            return Ok(new { Hash = tx.Hash });
        }

        /// <summary>
        /// Replace transaction by fee
        /// </summary>
        [HttpPost("{hash}/replace")]
        public async Task<IActionResult> ReplaceByFee(string hash, [FromBody] ReplaceByFeeRequest request)
        {
            var mempool = TransactionData.GetPool();
            var originalTx = mempool.FindOne(x => x.Hash == hash);

            if (originalTx == null)
                return Fail("NOT_FOUND", "Transaction not found in mempool.", 404);

            var result = await WalletService.SendTXOutRBF(
                originalTx.FromAddress,
                originalTx.ToAddress,
                originalTx.Amount,
                request.NewFee,
                hash);

            if (result == "FAIL" || result.Contains("FAIL"))
                return Fail("RBF_FAILED", result);

            return Ok(new { Result = result });
        }

        /// <summary>
        /// Get mempool transactions
        /// </summary>
        [HttpGet("mempool")]
        public IActionResult GetMempool()
        {
            var txs = TransactionData.GetMempool();
            return Ok(txs);
        }
    }
}
