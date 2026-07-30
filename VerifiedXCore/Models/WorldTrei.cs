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
