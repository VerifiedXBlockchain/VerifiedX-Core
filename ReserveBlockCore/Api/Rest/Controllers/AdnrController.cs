using Microsoft.AspNetCore.Mvc;
using ReserveBlockCore.Api.Rest.Infrastructure;
using ReserveBlockCore.Api.Rest.Models.Requests;
using ReserveBlockCore.Data;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Text.RegularExpressions;

namespace ReserveBlockCore.Api.Rest.Controllers
{
    public class AdnrController : RestBaseController
    {
        /// <summary>
        /// Create a domain name
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateAdnr([FromBody] CreateAdnrRequest request)
        {
            var wallet = AccountData.GetSingleAccount(request.Address);
            if (wallet == null)
                return Fail("NOT_FOUND", $"Account not found: {request.Address}", 404);

            var adnrDb = Adnr.GetAdnr();
            if (adnrDb == null)
                return Fail("ADNR_UNAVAILABLE", "ADNR database not available.");

            var existingAdnr = adnrDb.FindOne(x => x.Address == wallet.Address);
            if (existingAdnr != null)
                return Fail("ALREADY_EXISTS", $"This address already has a DNR: {existingAdnr.Name}");

            var name = request.Name.ToLower();

            if (name.Length > Globals.ADNRLimit)
                return Fail("NAME_TOO_LONG", "A DNR may only be a max of 65 characters.");

            if (!Regex.IsMatch(name, @"^[a-zA-Z0-9]+$"))
                return Fail("INVALID_NAME", "A DNR may only contain letters and numbers.");

            var nameVfx = name + ".vfx";
            var nameCheck = adnrDb.FindOne(x => x.Name == nameVfx);
            if (nameCheck != null)
                return Fail("NAME_TAKEN", $"This name is already taken: {nameVfx}");

            var result = await Adnr.CreateAdnrTx(request.Address, name);
            if (result.Item1 == null)
                return Fail("TX_FAILED", $"Transaction failed: {result.Item2}");

            return Created(new { Hash = result.Item1.Hash, Name = nameVfx });
        }

        /// <summary>
        /// Transfer ADNR to another address
        /// </summary>
        [HttpPost("transfer")]
        public async Task<IActionResult> TransferAdnr([FromBody] TransferAdnrRequest request)
        {
            var wallet = AccountData.GetSingleAccount(request.FromAddress);
            if (wallet == null)
                return Fail("NOT_FOUND", $"Account not found: {request.FromAddress}", 404);

            var adnrDb = Adnr.GetAdnr();
            if (adnrDb == null)
                return Fail("ADNR_UNAVAILABLE", "ADNR database not available.");

            var adnrCheck = adnrDb.FindOne(x => x.Address == wallet.Address);
            if (adnrCheck == null)
                return Fail("NO_ADNR", "This address does not have a DNR associated with it.");

            if (!AddressValidateUtility.ValidateAddress(request.ToAddress))
                return Fail("INVALID_ADDRESS", "To address is not a valid VFX address.");

            var toAddrAdnr = adnrDb.FindOne(x => x.Address == request.ToAddress);
            if (toAddrAdnr != null)
                return Fail("TARGET_HAS_ADNR", "To address already has an ADNR associated to it.");

            var result = await Adnr.TransferAdnrTx(request.FromAddress, request.ToAddress);
            if (result.Item1 == null)
                return Fail("TX_FAILED", $"Transaction failed: {result.Item2}");

            return Ok(new { Hash = result.Item1.Hash });
        }

        /// <summary>
        /// Delete ADNR from an address
        /// </summary>
        [HttpDelete("{address}")]
        public async Task<IActionResult> DeleteAdnr(string address)
        {
            var wallet = AccountData.GetSingleAccount(address);
            if (wallet == null)
                return Fail("NOT_FOUND", $"Account not found: {address}", 404);

            var adnrDb = Adnr.GetAdnr();
            if (adnrDb == null)
                return Fail("ADNR_UNAVAILABLE", "ADNR database not available.");

            var adnrCheck = adnrDb.FindOne(x => x.Address == wallet.Address);
            if (adnrCheck == null)
                return Fail("NO_ADNR", "This address does not have a DNR associated with it.");

            var result = await Adnr.DeleteAdnrTx(address);
            if (result.Item1 == null)
                return Fail("TX_FAILED", $"Transaction failed: {result.Item2}");

            return Ok(new { Hash = result.Item1.Hash });
        }

        /// <summary>
        /// Resolve a domain name to an address
        /// </summary>
        [HttpGet("resolve/{name}")]
        public IActionResult Resolve(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Fail("INVALID_NAME", "Name is required.");

            var lowerName = name.ToLower();

            if (lowerName.Contains(".vfx") || lowerName.Contains(".rbx"))
            {
                var result = Adnr.GetAddress(lowerName);
                if (result.Item1)
                    return Ok(new { Name = lowerName, Address = result.Item2 });

                return Fail("NOT_FOUND", "ADNR could not be resolved.", 404);
            }

            return Fail("INVALID_NAME", "Name must end with .vfx or .rbx");
        }

        /// <summary>
        /// Reverse lookup — address to domain name
        /// </summary>
        [HttpGet("reverse/{address}")]
        public IActionResult ReverseLookup(string address)
        {
            if (string.IsNullOrEmpty(address))
                return Fail("INVALID_ADDRESS", "Address is required.");

            var adnrName = Adnr.GetAdnr(address);
            if (!string.IsNullOrEmpty(adnrName))
                return Ok(new { Address = address, Name = adnrName });

            return Fail("NOT_FOUND", "No ADNR found for this address.", 404);
        }
    }
}
