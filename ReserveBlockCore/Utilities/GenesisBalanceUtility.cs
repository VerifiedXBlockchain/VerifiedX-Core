namespace ReserveBlockCore.Utilities
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
                    {"xMpa8DxDLdC9SQPcAFBc2vqwyPsoFtrWyC", 25_000_000M},
                    {"xBRzJUZiXjE3hkrpzGYMSpYCHU1yPpu8cj", 10_000_000M },
                    {"xBRNST9oL8oW6JctcyumcafsnWCVXbzZnr", 25_000_000M },
                    {"x9hjkeNB6t2qJzbZaayMMxmfVGZh5XpmW7",  20_000_000M}
                }; // Address, Amount in Decimal
            }

            return balanceSheet;
        }
        

    }
}
