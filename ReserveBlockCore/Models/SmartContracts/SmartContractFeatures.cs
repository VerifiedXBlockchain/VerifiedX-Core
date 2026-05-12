namespace ReserveBlockCore.Models
{
    public class SmartContractFeatures
    {
        public FeatureName FeatureName { get; set; } //Royalty, Evolving, Music, Ticket, etc.
        public object FeatureFeatures { get; set; }
    }

    //Do not change order.
    public enum FeatureName
    { 
        Evolving, //returns a list of EvolvingFeatures
        Royalty, // returns a class of RoyaltyFeatures
        MultiAsset,
        Tokenization,//class - vBTC v1 (arbiter-based)
        Music, //Class with a list of songs
        MultiOwner, //List of MultiOwnerFeatures
        SelfDestruct,//class
        Consumable,//class
        Fractionalized,//class
        Paired,//class
        Wrapped,//class
        Soulbound,//class
        Ticket,//class
        Token, //Tokens
        TokenizationV2 //class - vBTC v2 (MPC-based)
    }
}
