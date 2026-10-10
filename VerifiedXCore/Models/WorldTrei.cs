using VerifiedXCore.Extensions;
using VerifiedXCore.Data;

namespace VerifiedXCore.Models
{
    public class WorldTrei
    {
        public long Id { get; set; }
        public string StateRoot { get; set; }

        /// <summary>Hash of <c>DB_Privacy</c> shielded pool state after the block (see <see cref="VerifiedXCore.Privacy.ShieldedStateRoot.Compute"/>).</summary>
        public string ShieldedStateRoot { get; set; } = "";
        /// <summary>
        /// Fund-loss audit item 1 (burn): the block height at which the forged-VFX burn was applied to THIS state; 0 when it
        /// has not been. It lives in the state it describes: a full rebuild wipes it with the balances, a snapshot copies
        /// it and a restore brings back the value that matches the restored balances (see StateData.ApplyForgedVfxBurnsAsync).
        /// </summary>
        public long ForgedVfxBurnAppliedHeight { get; set; }
        public static WorldTrei GetWorldTreiRecord()
        {
            var wTrei = DbContext.DB_WorldStateTrei.GetCollection<WorldTrei>(DbContext.RSRV_WSTATE_TREI);
            var worldState = wTrei.FindOne(x => true);
            return worldState;
        }

        public static void UpdateWorldTrei(Block block)
        {
            try
            {
                var wTrei = GetWorldTrei();
                var record = wTrei.FindOne(x => true);
                if (record == null)
                {
                    var worldTrei = new WorldTrei
                    {
                        StateRoot = block.StateRoot,
                        ShieldedStateRoot = global::VerifiedXCore.Privacy.ShieldedStateRoot.Compute(),
                    };
                    wTrei.InsertSafe(worldTrei);
                }
                else
                {
                    record.StateRoot = block.StateRoot;
                    record.ShieldedStateRoot = global::VerifiedXCore.Privacy.ShieldedStateRoot.Compute();
                    wTrei.UpdateSafe(record);
                }
            }
            catch { }
        }

        public static LiteDB.ILiteCollection<WorldTrei> GetWorldTrei()
        {
            var wTrei = DbContext.DB_WorldStateTrei.GetCollection<WorldTrei>(DbContext.RSRV_WSTATE_TREI);
            return wTrei;
        }
    }

    
}
