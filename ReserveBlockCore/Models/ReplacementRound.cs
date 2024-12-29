using ReserveBlockCore.Utilities;
using System.Security.Cryptography;

namespace ReserveBlockCore.Models
{
    public class ReplacementRound
    {
        public Dictionary<string, int> CasterSeeds { get; set; }
        public NetworkValidator? MyChosenCaster { get; set; }
        public int SeedPiece { get; set; }
        public long StartTime { get; set; }
        public long EndTime { get; set; }
        public Dictionary<string, NetworkValidator?> NetworkValidators { get; set; }
        public bool IsFinalized { get; set; }

        public ReplacementRound()
        {
            CasterSeeds = new Dictionary<string, int>();
            NetworkValidators = new Dictionary<string, NetworkValidator?>();
            MyChosenCaster = null;

            foreach (var caster in Globals.BlockCasters)
            {
                CasterSeeds.Add(caster.PeerIP, 0);
                if (caster.ValidatorAddress == Globals.ValidatorAddress)
                    NetworkValidators.Add("local", null);
                else
                    NetworkValidators.Add(caster.PeerIP, null);
            }

            SeedPiece = RandomNumberGenerator.GetInt32(99999999);
            StartTime = TimeUtil.GetTime();
            EndTime = TimeUtil.GetTime(0, 3);//adding three minutes for end.
        }
    }
}
