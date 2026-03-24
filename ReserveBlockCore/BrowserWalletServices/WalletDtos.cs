namespace ReserveBlockCore.BrowserWalletServices
{
    public class SendRequest
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Amount { get; set; } = "";
    }

    public class VBTCWDRequest
    {
        public string ScUID { get; set; } = "";
        public string OwnerAddress { get; set; } = "";
        public string BTCAddress { get; set; } = "";
        public string Amount { get; set; } = "";
        public string FeeRate { get; set; } = "10";
    }

    public class VBTCWDComplete
    {
        public string ScUID { get; set; } = "";
        public string RequestHash { get; set; } = "";
    }

    public class VBTCTransferRequest
    {
        public string ScUID { get; set; } = "";
        public string FromAddress { get; set; } = "";
        public string ToAddress { get; set; } = "";
        public string Amount { get; set; } = "";
    }

    public class VBTCShieldRequest
    {
        public string FromAddress { get; set; } = "";
        public string ZfxAddress { get; set; } = "";
        public string ScUID { get; set; } = "";
        public string Amount { get; set; } = "";
    }

    public class VBTCPrivacyUnshieldRequest
    {
        public string ZfxAddress { get; set; } = "";
        public string ToAddress { get; set; } = "";
        public string ScUID { get; set; } = "";
        public string Amount { get; set; } = "";
        public string? Password { get; set; }
    }

    public class VBTCPrivacyTransferRequest
    {
        public string FromZfxAddress { get; set; } = "";
        public string ToZfxAddress { get; set; } = "";
        public string ScUID { get; set; } = "";
        public string Amount { get; set; } = "";
        public string? Password { get; set; }
    }

    public class BtcLinkEvmRequest
    {
        public string BtcAddress { get; set; } = "";
        public string? EvmAddress { get; set; }
    }
}
