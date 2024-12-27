using ReserveBlockCore.Utilities;

namespace ReserveBlockCore.Models
{
    public class ReplacementRound
    {
        public Dictionary<string, int> CasterSeeds { get; set; }
        public string WinningAddress { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }

        public ReplacementRound()
        {
            CasterSeeds = new Dictionary<string, int>();
            foreach(var caster in Globals.BlockCasters)
            {
                CasterSeeds.Add(caster.PeerIP, 0);
            }

            StartTime = TimeUtil.GetTime();
            EndTime = TimeUtil.GetTime(0, 2);//adding two minutes for end.
        }
    }
}
