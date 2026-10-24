namespace VerifiedXCore.Utilities
{
    public class GenesisBalanceUtility
    {
        public static Dictionary<string, decimal> GenesisBalances()
        {
            
            Dictionary<string, decimal> balanceSheet = new Dictionary<string, decimal> {
                    {"Insert Address", 1.0M },// Address, Amount in Decimal
                                
            };

            if(Globals.IsTestNet == true)
            {
                balanceSheet = new Dictionary<string, decimal> {
                    {"xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC", 20_000_000M}, //bootstrap
                    {"xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj", 10_000_000M }, //bootstrap
                    {"xBRNST9oL8oW6JctcyumcafsnWCVXbzZnr", 20_000_000M }, //bootstrap
                    {"x9hjkeNB6t2qJzbZaayMMxmfVGZh5XpmW7",  20_000_000M},
                    {"xCkUC4rrh2AnfNf78D5Ps83pMywk5vrwpi", 5_500M },
                    {"x8NemQSjMUKXaLqbpVU6YNq4P2d6Jbcrra", 15_000_000M }, //faucet
                    {"xM95SoTttvaSnYzLmaRdg4C5uiVgCxwxLT", 5_001M }, // Trinculo
                    {"xETDW4592mg37JwNJX8xpTHC6jtk6AVKa2", 5_001M }, // Kari
                    {"xRSb5QWKb7Syh36kdXqkDvGoTgwa2EYeUE", 5_001M }, // Deimos
                    {"x9Ctb7ftaNdNGE9hypzwyHRm9sUHU7g6EG", 5_001M }, // Telesto
                    {"xLy7gsRzoL57obiLxyCDF7BKTgXoSw3WvZ", 5_001M }, // Narvi
                    {"xC6n2hmPQYmi7e4z5L7aag4iLw5ZMzzYgA", 5_001M }, // Mundilfari
                    {"xKdsCn6zzGCoViya8XpyV5ZP6tyUqLvKfM", 5_001M }, // Metis
                    {"x8Yt2A2D4bVgXLuxmY2FPeKjMYeL84135Z", 5_001M }, // Phobos
                    {"xU28Y2Z4cAs2VW11Cix8W41xxReEod65Gv", 5_001M }, // Caliban
                    {"xAz79dkLgm9A9YUNKaYXcSR67JJHLakqtC", 5_001M } // Greip
                }; // Address, Amount in Decimal
            }

            return balanceSheet;
        }
        

    }
}
