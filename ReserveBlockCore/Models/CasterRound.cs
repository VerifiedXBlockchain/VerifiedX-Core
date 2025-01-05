namespace ReserveBlockCore.Models
{
    public class CasterRound
    {
        public long BlockHeight { get; set; }
        public string Validator { get; set; }
        public int RoundAttempts { get; set; }

        public CasterRound()
        {
            RoundAttempts = 0;
        }

        public void ProgressRound()
        {
            RoundAttempts++;
        }

        public bool RoundStale()
        {
            if(RoundAttempts >= 3)
            {
                return true;
            }

            return false;
        }
    }
}
