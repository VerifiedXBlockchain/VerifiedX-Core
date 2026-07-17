using ReserveBlockCore.Bitcoin.Models;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.BrowserWalletServices
{
    public static class WalletVbtcService
    {
        public static object GetBitcoinAccounts()
        {
            var accounts = BitcoinAccount.GetBitcoinAccounts();
            if (accounts == null || !accounts.Any())
                return Array.Empty<object>();

            return accounts.Select(a => new
            {
                address = a.Address,
                adnr = a.ADNR,
                balance = a.Balance,
                isValidating = a.IsValidating,
                linkedEvmAddress = a.LinkedEvmAddress
            }).ToList();
        }

        public static object GetVBTCContracts(string address)
        {
            var scStates = SmartContractStateTrei.GetvBTCSmartContracts(address);
            if (scStates == null || !scStates.Any())
                return Array.Empty<object>();

            var resultList = new List<object>();
            var seen = new HashSet<string>();

            foreach (var scState in scStates)
            {
                bool isOwner = scState.OwnerAddress == address;

                decimal ledgerBalance = 0M;
                if (scState.SCStateTreiTokenizationTXes != null && scState.SCStateTreiTokenizationTXes.Any())
                {
                    var transactions = scState.SCStateTreiTokenizationTXes
                        .Where(x => x.FromAddress == address || x.ToAddress == address);

                    // For owners, exclude burn entries (ToAddress == "-") to avoid double-counting
                    // with the deposit address balance that already reflects withdrawals
                    if (isOwner)
                        transactions = transactions.Where(x => x.ToAddress != "-");

                    var txList = transactions.ToList();
                    if (txList.Any())
                        ledgerBalance = txList.Sum(x => x.Amount);
                }

                decimal totalBalance = ledgerBalance;
                string depositAddress = "";

                if (isOwner)
                {
                    var contract = VBTCContractV2.GetContract(scState.SmartContractUID);
                    if (contract != null)
                    {
                        depositAddress = contract.DepositAddress ?? "";
                        totalBalance = contract.Balance + ledgerBalance;
                    }
                }

                if (!seen.Add(scState.SmartContractUID))
                    continue;

                if (totalBalance > 0M || isOwner)
                {
                    var contract = VBTCContractV2.GetContract(scState.SmartContractUID);
                    resultList.Add(new
                    {
                        scUID = scState.SmartContractUID,
                        ownerAddress = scState.OwnerAddress,
                        depositAddress = depositAddress,
                        balance = totalBalance,
                        ledgerBalance = ledgerBalance,
                        isOwner = isOwner,
                        withdrawalStatus = contract?.WithdrawalStatus.ToString() ?? "None",
                        activeWithdrawalAmount = contract?.ActiveWithdrawalAmount ?? 0M,
                        activeWithdrawalDest = contract?.ActiveWithdrawalBTCDestination ?? "",
                        proofBlockHeight = contract?.ProofBlockHeight ?? 0,
                        totalValidators = contract?.TotalRegisteredValidators ?? 0,
                        requiredThreshold = contract?.RequiredThreshold ?? 0,
                        // S3C §1/§5.4 disclosure: surface IsS3C (resolved via state trei so transferees
                        // holding no local record still see it) + the companion back-pointer for grouping.
                        isS3C = Bitcoin.Services.VBTCService.ResolveContractIsS3C(scState.SmartContractUID),
                        linkedContractUID = contract?.LinkedContractUID
                    });
                }
            }

            return resultList;
        }

        public static async Task<(bool success, string message)> RequestWithdrawal(
            string scUID, string ownerAddress, string btcAddress, decimal amount, int feeRate)
        {
            var result = await Bitcoin.Services.VBTCService.RequestWithdrawal(scUID, ownerAddress, btcAddress, amount, feeRate);
            return (result.Item1, result.Item2);
        }

        public static async Task<(bool success, string message, string? vfxTxHash, string? btcTxHash)> CompleteWithdrawal(
            string scUID, string requestHash)
        {
            var result = await Bitcoin.Services.VBTCService.CompleteWithdrawal(scUID, requestHash);
            if (result.Success)
                return (true, "Withdrawal completed!", result.VFXTxHash, result.BTCTxHash);
            else
                return (false, result.ErrorMessage, null, null);
        }

        public static async Task<(bool success, string message)> CancelWithdrawal(
            string scUID, string ownerAddress, string requestHash)
        {
            var result = await Bitcoin.Services.VBTCService.CancelWithdrawal(scUID, ownerAddress, requestHash);
            return (result.Item1, result.Item2);
        }

        public static object GetWithdrawStatus(string scUID)
        {
            var contract = VBTCContractV2.GetContract(scUID);
            if (contract == null)
                return new { success = false, message = "Contract not found" };

            return new
            {
                success = true,
                status = contract.WithdrawalStatus.ToString(),
                amount = contract.ActiveWithdrawalAmount ?? 0M,
                destination = contract.ActiveWithdrawalBTCDestination ?? "",
                requestHash = contract.ActiveWithdrawalRequestHash ?? ""
            };
        }

        public static async Task<(bool success, string message)> TransferVBTC(
            string scUID, string fromAddress, string toAddress, decimal amount)
        {
            var addrValid = AddressValidateUtility.ValidateAddress(toAddress);
            if (!addrValid)
                return (false, "Invalid destination VFX address.");

            var result = await Bitcoin.Services.VBTCService.TransferVBTC(scUID, fromAddress, toAddress, amount);
            return (result.Item1, result.Item2);
        }

        /// <summary>
        /// Bridge vBTC to Base as vBTC.b (ERC-20). 
        /// User-driven: creates lock TX on VFX, collects validator attestations, 
        /// and submits mintWithProof on Base using the user's own ETH key (user pays gas).
        /// </summary>
        public static async Task<object> BridgeToBase(string scUID, string ownerAddress, decimal amount, string evmDestination)
        {
            try
            {
                var result = await Bitcoin.Services.UserBridgeMintService.ExecuteBridgeToBase(scUID, ownerAddress, amount, evmDestination);

                if (result.Success)
                {
                    return new
                    {
                        success = true,
                        message = result.Message,
                        lockId = result.LockId,
                        amount = amount,
                        evmDestination = evmDestination,
                        contractAddress = Bitcoin.Services.BaseBridgeService.ContractAddress,
                        chainId = Bitcoin.Services.BaseBridgeService.BaseChainId
                    };
                }
                else
                {
                    return new { success = false, message = result.Message };
                }
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Error bridging to Base: {ex.Message}" };
            }
        }

        /// <summary>
        /// Retry a failed or timed-out bridge mint for a specific lock ID.
        /// </summary>
        public static async Task<object> RetryBridgeMint(string lockId, string ownerAddress)
        {
            try
            {
                var result = await Bitcoin.Services.UserBridgeMintService.RetryMintForLock(lockId, ownerAddress);
                return new { success = result.Success, message = result.Message };
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Error retrying mint: {ex.Message}" };
            }
        }

        /// <summary>
        /// Force retry a stuck bridge mint. Reconstructs the local BridgeLockRecord from on-chain
        /// consensus state if missing, skips the wait-for-confirmation step, and goes straight to
        /// collecting validator attestations and submitting mintWithProof on Base.
        /// </summary>
        public static async Task<object> ForceRetryBridgeMint(string lockId, string ownerAddress)
        {
            try
            {
                var result = await Bitcoin.Services.UserBridgeMintService.ForceRetryMintForLock(lockId, ownerAddress);
                return new { success = result.Success, message = result.Message };
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Error force-retrying mint: {ex.Message}" };
            }
        }

        /// <summary>
        /// Pre-flight info for the Bridge to Base modal.
        /// Returns derived Base address, ETH balance, vBTC.b balance, available vBTC, and bridge config status.
        /// </summary>
        public static async Task<object> GetBridgePreflight(string ownerAddress, string scUID)
        {
            try
            {
                // Derive Base address from the VFX key
                var derivedBaseAddress = Bitcoin.Services.ValidatorEthKeyService.DeriveBaseAddressFromAccount(ownerAddress);
                var hasDerivedAddress = !string.IsNullOrEmpty(derivedBaseAddress);

                // Bridge config status
                var bridgeConfigured = Bitcoin.Services.BaseBridgeService.IsBridgeConfigured;
                var canReadEth = Bitcoin.Services.BaseBridgeService.CanReadEth;
                var canReadVbtc = Bitcoin.Services.BaseBridgeService.CanReadVbtcToken;
                var networkName = Bitcoin.Services.BaseBridgeService.BaseNetworkDisplayName;
                var chainId = Bitcoin.Services.BaseBridgeService.BaseChainId;
                var contractAddress = Bitcoin.Services.BaseBridgeService.ContractAddress;

                // Fetch vBTC available balance on VFX side
                decimal availableVbtc = 0M;
                string vbtcError = null;
                try
                {
                    var balResult = await Bitcoin.Services.VBTCService.TryGetAvailableTransparentVbtcBalance(scUID, ownerAddress);
                    if (balResult.success)
                    {
                        // Subtract local bridge reserves not yet confirmed on-chain
                        var reserved = Bitcoin.Models.BridgeLockRecord.GetLockedAmount(ownerAddress, scUID);
                        availableVbtc = balResult.availableBalance - reserved;
                        if (availableVbtc < 0) availableVbtc = 0;
                    }
                    else
                    {
                        vbtcError = balResult.error;
                    }
                }
                catch (Exception ex)
                {
                    vbtcError = ex.Message;
                }

                // Fetch ETH balance on Base for the derived address
                decimal? ethBalance = null;
                string ethError = null;
                if (hasDerivedAddress && canReadEth)
                {
                    try
                    {
                        var ethResult = await Bitcoin.Services.BaseBridgeService.GetEthBalanceAsync(derivedBaseAddress);
                        if (ethResult.Success)
                            ethBalance = ethResult.BalanceEth;
                        else
                            ethError = ethResult.Message;
                    }
                    catch (Exception ex) { ethError = ex.Message; }
                }

                // Fetch vBTC.b balance on Base for the derived address
                decimal? vbtcBBalance = null;
                string vbtcBError = null;
                if (hasDerivedAddress && canReadVbtc)
                {
                    try
                    {
                        var tokResult = await Bitcoin.Services.BaseBridgeService.GetBaseBalance(derivedBaseAddress);
                        if (tokResult.Success)
                            vbtcBBalance = tokResult.Balance;
                        else
                            vbtcBError = tokResult.Message;
                    }
                    catch (Exception ex) { vbtcBError = ex.Message; }
                }

                return new
                {
                    success = true,
                    // VFX side
                    ownerAddress = ownerAddress,
                    scUID = scUID,
                    availableVbtc = availableVbtc,
                    vbtcError = vbtcError,
                    // Derived Base address
                    derivedBaseAddress = derivedBaseAddress ?? "",
                    hasDerivedAddress = hasDerivedAddress,
                    // Base balances
                    ethBalance = ethBalance,
                    ethError = ethError,
                    vbtcBBalance = vbtcBBalance,
                    vbtcBError = vbtcBError,
                    // Config
                    bridgeConfigured = bridgeConfigured,
                    canReadEth = canReadEth,
                    canReadVbtc = canReadVbtc,
                    networkName = networkName,
                    chainId = chainId,
                    contractAddress = contractAddress
                };
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Preflight error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Get the status of a bridge lock by lockId.
        /// Returns the BridgeLockRecord including attestation progress and status.
        /// </summary>
        public static object GetBridgeLockStatus(string lockId)
        {
            try
            {
                var record = BridgeLockRecord.GetByLockId(lockId);
                if (record == null)
                    return new { success = false, message = $"Bridge lock not found: {lockId}" };

                var sigCount = record.ValidatorSignatures?.Count ?? 0;

                return new
                {
                    success = true,
                    lockId = record.LockId,
                    scUID = record.SmartContractUID,
                    ownerAddress = record.OwnerAddress,
                    amount = record.Amount,
                    amountSats = record.AmountSats,
                    evmDestination = record.EvmDestination,
                    status = record.Status.ToString(),
                    vfxLockTxHash = record.VfxLockTxHash,
                    vfxLockConfirmedOnChain = record.VfxLockConfirmedOnChain,
                    vfxLockBlockHeight = record.VfxLockBlockHeight,
                    baseTxHash = record.BaseTxHash,
                    exitBurnTxHash = record.ExitBurnTxHash,
                    signaturesCollected = sigCount,
                    requiredSignatures = record.RequiredSignatures,
                    mintNonce = record.MintNonce,
                    signatures = record.ValidatorSignatures,
                    createdAtUtc = record.CreatedAtUtc,
                    relayedAtUtc = record.RelayedAtUtc,
                    finalizedAtUtc = record.FinalizedAtUtc,
                    errorMessage = record.ErrorMessage
                };
            }
            catch (Exception ex)
            {
                return new { success = false, message = $"Error retrieving bridge lock status: {ex.Message}" };
            }
        }
    }
}
