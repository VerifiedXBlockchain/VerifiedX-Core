using Newtonsoft.Json.Linq;
using VerifiedXCore.Utilities;

namespace VerifiedXCore.Bitcoin.Services
{
    /// <summary>
    /// Asked before this node makes a bridge lock (VBTCService.CreateBridgeLockTx, which every local way of locking goes
    /// through): can the vBTCb contract's signers mint it? A lock debits its holder on VFX at once, and only a mint on Base
    /// gives that back as vBTC.b. The contract mints with 2/3 of the validators it lists and a majority of the casters it
    /// lists, and its lists follow VFX days behind; when too few of the listed signers are running, VFX still takes the
    /// lock and Base cannot mint it. So the node reads the contract's lists, asks each listed signer's node whether it is
    /// up and on the network's chain (its Health answer, the conditions it signs a mint under), and makes the lock only
    /// when those that answer meet both of the contract's thresholds. A listed signer this node has no address for (no
    /// registration in its window, no caster record that names it) was not asked and is not held against the lock: the
    /// lock is refused on what the node sees, not on what it cannot see. A contract it cannot read at all is the
    /// exception: then nothing is known, and nothing is locked until it can be read.
    ///
    /// A check by this node for its own user, not a block rule: a lock sent as a raw transaction, or from another node, is
    /// judged by consensus alone. It says the signers answer now; the mint is signed some minutes later, once the lock is
    /// deep enough.
    /// </summary>
    internal static class BaseMintReadiness
    {
        /// <summary>How long a signer's node has to answer.</summary>
        internal static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(5);

        /// <summary>Why a bridge lock is not made now, or null when Base can mint it (or this node has no bridge contract to ask).</summary>
        internal static async Task<string?> RefusalAsync()
        {
            if (!BaseBridgeService.IsBridgeConfigured) return null;
            var view = await BaseBridgeService.ReadSignerViewAsync();
            if (view == null)
                return "Could not read the Base contract's signers, so it is not known whether Base can mint this lock. Nothing was locked; try again.";
            if (view.MessageVersion is not (3 or VbtcbTypedData.MessageVersion))
                return $"The Base contract reads message version {view.MessageVersion}, which this node cannot collect a mint for. Nothing was locked.";

            var signers = UserBridgeMintService.ContractSigners(view);
            var asked = await Task.WhenAll(signers.Select(async s => (s.BaseAddress, Answers: await AnswersAsync(s.IPAddress))));
            var answering = asked.Where(a => a.Answers).Select(a => a.BaseAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var known = signers.Select(s => s.BaseAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var notAsked = view.Validators.Concat(view.CasterList).Where(a => !known.Contains(a)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (UserBridgeMintService.Enough(view, view.RequiredMintSignatures, answering.Concat(notAsked))) return null;

            var why = $"Base cannot mint a bridge lock right now: {view.Validators.Count(answering.Contains)} of the contract's {view.Validators.Count} validators"
                + $" and {view.CasterList.Count(answering.Contains)} of its {view.CasterList.Count} casters answer, and it needs {view.RequiredMintSignatures} and {view.RequiredCasterSignatures}"
                + (notAsked.Count > 0 ? $" ({notAsked.Count} more could not be asked: this node has no address for them)." : ".")
                + " Nothing was locked; try again later.";
            LogUtility.Log($"[BaseMintReadiness] {why}", "BaseMintReadiness.RefusalAsync");
            return why;
        }

        /// <summary>
        /// Whether the node at <paramref name="ip"/> answers and reports what a signer needs to sign a mint
        /// (BaseBridgeAttestationService.NotReadyToAttest): a validator, synced, not stalled or recovering, and not on
        /// another chain than its peers.
        /// </summary>
        private static async Task<bool> AnswersAsync(string ip)
        {
            try
            {
                var text = await NodeHttp.GetForShortReplyAsync($"http://{ip.Replace("::ffff:", "")}:{Globals.ValAPIPort}/valapi/validator/Health", timeout: AnswerWithin);
                if (text == null) return false;
                var health = BoundedJson.Parse(text, BoundedJson.ValidatorApi);
                if (health.Type == JTokenType.String) health = BoundedJson.Parse(health.Value<string>() ?? "", BoundedJson.ValidatorApi);
                if (health is not JObject node) return false;
                return node.Value<bool?>("IsValidator") == true && node.Value<bool?>("IsChainSynced") == true
                    && (node.Value<string>("SyncState") is null or "Healthy")
                    && (node.Value<string>("ForkStatus") is not ("Resolving" or "Unresolved"));
            }
            catch { return false; }
        }
    }
}
