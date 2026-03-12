using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.Services;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class SignaturesController : RestBaseController
    {
        /// <summary>
        /// Create a signature (address + message in body)
        /// </summary>
        [HttpPost]
        public IActionResult CreateSignature([FromBody] CreateSignatureRequest request)
        {
            var account = AccountData.GetSingleAccount(request.Address);
            if (account == null)
                return Fail("NOT_FOUND", $"Account not found: {request.Address}", 404);

            var signature = SignatureService.CreateSignature(request.Message, account.GetPrivKey, account.PublicKey);

            return Created(new { Address = request.Address, Message = request.Message, Signature = signature });
        }

        /// <summary>
        /// Verify a signature (all params in body)
        /// </summary>
        [HttpPost("verify")]
        public IActionResult VerifySignature([FromBody] VerifySignatureRequest request)
        {
            var result = SignatureService.VerifySignature(request.Address, request.Message, request.Signature);

            return Ok(new { Verified = result, Address = request.Address });
        }
    }
}
