namespace ReserveBlockCore.Models.Privacy
{
    public class ShieldVfxRequest
    {
        public string FromAddress { get; set; } = "";
        public decimal ShieldAmount { get; set; }
        public decimal? TransparentFee { get; set; }
        public string RecipientZfxAddress { get; set; } = "";
        public string? Memo { get; set; }
    }

    public class UnshieldVfxRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
        public string TransparentToAddress { get; set; } = "";
        public decimal TransparentAmount { get; set; }
    }

    public class PrivateTransferVfxRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
        public string RecipientZfxAddress { get; set; } = "";
        public decimal PaymentAmount { get; set; }
    }

    /// <summary>Merges the two smallest unspent VFX notes into one via Z→Z to the same <c>zfx_</c> address (repeat to fold more dust).</summary>
    public class ConsolidateShieldedVfxRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
    }

    public class GenerateShieldedAddressRequest
    {
        /// <summary>When true, uses <see cref="HDWallet"/> seed from local DB (must exist).</summary>
        public bool UseLocalHdWallet { get; set; }
        public string? WalletSeedHex { get; set; }
        public uint CoinType { get; set; } = 889;
        public uint AddressIndex { get; set; }
    }

    public class ScanShieldedRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
        public long FromHeight { get; set; }
        public long ToHeight { get; set; }
    }

    /// <summary>Wipes cached notes/balances and rescans from FromHeight to rebuild. Fixes corrupted balances.</summary>
    public class ResyncShieldedWalletRequest
    {
        public string ZfxAddress { get; set; } = "";
        public long FromHeight { get; set; }
        public long ToHeight { get; set; }
    }

    public class ExportViewingKeyRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
    }

    public class ImportViewingKeyRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string ViewingKeyBase64 { get; set; } = "";
        public string? TransparentSourceAddress { get; set; }
    }

    public class ShieldVbtcRequest
    {
        public string FromAddress { get; set; } = "";
        public string VbtcContractUid { get; set; } = "";
        public decimal VbtcAmount { get; set; }
        public decimal? TransparentFee { get; set; }
        public string RecipientZfxAddress { get; set; } = "";
        public string? Memo { get; set; }
    }

    public class UnshieldVbtcRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
        public string VbtcContractUid { get; set; } = "";
        public string TransparentToAddress { get; set; } = "";
        public decimal TransparentVbtcAmount { get; set; }
    }

    public class PrivateTransferVbtcRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
        public string VbtcContractUid { get; set; } = "";
        public string RecipientZfxAddress { get; set; } = "";
        public decimal PaymentAmount { get; set; }
    }

    /// <summary>Merges the two smallest unspent vBTC notes into one via Z→Z to the same <c>zfx_</c> address.</summary>
    public class ConsolidateShieldedVbtcRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
        public string VbtcContractUid { get; set; } = "";
    }

    public class ScanShieldedVbtcRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string? WalletPassword { get; set; }
        public string VbtcContractUid { get; set; } = "";
        public long FromHeight { get; set; }
        public long ToHeight { get; set; }
    }

    /// <summary>Wipes cached vBTC notes/balances for a specific contract and rescans from FromHeight.</summary>
    public class ResyncShieldedVbtcRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string VbtcContractUid { get; set; } = "";
        public long FromHeight { get; set; }
        public long ToHeight { get; set; }
    }
}
