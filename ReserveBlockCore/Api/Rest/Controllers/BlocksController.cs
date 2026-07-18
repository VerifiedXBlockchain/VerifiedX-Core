using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class BlocksController : RestBaseController
    {
        /// <summary>
        /// List recent blocks (paginated)
        /// </summary>
        [HttpGet]
        public IActionResult GetAll([FromQuery] PaginationParams paging)
        {
            var lastBlock = Globals.LastBlock;
            if (lastBlock == null || lastBlock.Height < 0)
                return OkPaged(Enumerable.Empty<object>(), paging.Page, paging.PageSize, 0);

            var totalCount = (int)(lastBlock.Height + 1);
            var skip = (paging.Page - 1) * paging.PageSize;
            var startHeight = Math.Max(0, lastBlock.Height - skip - paging.PageSize + 1);
            var endHeight = lastBlock.Height - skip;

            if (endHeight < 0)
                return OkPaged(Enumerable.Empty<object>(), paging.Page, paging.PageSize, totalCount);

            var blocks = BlockchainData.GetBlocks();
            var result = blocks.Query()
                .Where(b => b.Height >= startHeight && b.Height <= endHeight)
                .OrderByDescending(b => b.Height)
                .Limit(paging.PageSize)
                .ToList()
                .Select(b => new
                {
                    b.Height,
                    b.Hash,
                    b.Timestamp,
                    b.Validator,
                    b.NumOfTx,
                    b.TotalReward,
                    b.TotalAmount,
                    b.Size,
                    b.BCraftTime,
                    b.Version
                });

            return OkPaged(result, paging.Page, paging.PageSize, totalCount);
        }

        /// <summary>
        /// Get the latest block
        /// </summary>
        [HttpGet("latest")]
        public IActionResult GetLatest()
        {
            var block = Globals.LastBlock;
            if (block == null || block.Height < 0)
                return Fail("NOT_FOUND", "No blocks available.", 404);

            return Ok(block);
        }

        /// <summary>
        /// Get block by height
        /// </summary>
        [HttpGet("{height:long}")]
        public IActionResult GetByHeight(long height)
        {
            var block = BlockchainData.GetBlockByHeight(height);
            if (block == null)
                return Fail("NOT_FOUND", $"Block not found at height {height}.", 404);

            return Ok(block);
        }

        /// <summary>
        /// Get block by hash
        /// </summary>
        [HttpGet("hash/{hash}")]
        public IActionResult GetByHash(string hash)
        {
            var block = BlockchainData.GetBlockByHash(hash);
            if (block == null)
                return Fail("NOT_FOUND", $"Block not found with hash {hash}.", 404);

            return Ok(block);
        }
    }
}
