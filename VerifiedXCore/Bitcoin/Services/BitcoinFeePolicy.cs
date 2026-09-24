namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// VX-18: bounds on the Bitcoin fee a local signing path will pay. ReplaceByFee (and the other send paths) took the
    /// caller's fee rate unchecked and multiplied it straight into NBitcoin's FeeRate, so one request could hand the
    /// whole input set to miners. The rate must be positive and at most <see cref="Globals.MaxBtcFeeRateSatPerVb"/>
    /// (config "MaxBtcFeeRateSatPerVb", default 2,000 sat/vB — far above any real mempool, far below "everything").
    /// </summary>
    public static class BitcoinFeePolicy
    {
        public const long DefaultMaxFeeRateSatPerVb = 2_000;

        /// <summary>RBF: the replacement's total fee may not exceed this share of the amount sent unless explicitly allowed.</summary>
        public const decimal MaxFeeShareOfAmount = 0.10M;

        public static bool TryValidateFeeRate(long feeRateSatPerVb, out string error)
        {
            error = "";
            var max = Globals.MaxBtcFeeRateSatPerVb > 0 ? Globals.MaxBtcFeeRateSatPerVb : DefaultMaxFeeRateSatPerVb;
            if (feeRateSatPerVb <= 0)
            {
                error = "Fee rate must be greater than zero.";
                return false;
            }
            if (feeRateSatPerVb > max)
            {
                error = $"Fee rate {feeRateSatPerVb} sat/vB exceeds the maximum of {max} sat/vB (config MaxBtcFeeRateSatPerVb).";
                return false;
            }
            return true;
        }

        public static bool TryValidateTotalFee(ulong feeSats, ulong amountSats, bool allowHighFee, out string error)
        {
            error = "";
            if (allowHighFee)
                return true;
            if ((decimal)feeSats > (decimal)amountSats * MaxFeeShareOfAmount)
            {
                error = $"Fee of {feeSats} sats is more than {MaxFeeShareOfAmount:P0} of the amount ({amountSats} sats). Pass allowHighFee=true to send anyway.";
                return false;
            }
            return true;
        }

        /// <summary>True when the wallet is encrypted and its password is not in memory (no signing).</summary>
        public static bool WalletIsLocked() =>
            Globals.IsWalletEncrypted && (Globals.EncryptPassword == null || Globals.EncryptPassword.Length == 0);
    }
}
