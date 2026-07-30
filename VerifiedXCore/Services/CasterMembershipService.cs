using Newtonsoft.Json;
using VerifiedXCore.Data;
using VerifiedXCore.Models;
using VerifiedXCore.Utilities;
using System.Text;

namespace VerifiedXCore.Services
{
    /// <summary>
    /// Wave 3: rotation orchestration on top of <see cref="CasterMembershipStore"/>.
    /// RECORD-FIRST rule: a caster-set change (promotion/demotion/departure) first becomes a
    /// majority-signed membership record; only then is the live BlockCasters bag mutated.
    /// If signature collection fails, the change is aborted and the set stays consistent.
    /// All methods no-op (returning true) while the record era is inactive, so legacy flows
    /// are untouched until a genesis record is minted at the coordinated restart (Wave 6).
    /// </summary>
    public static class CasterMembershipService
    {
        /// <summary>Margin so a new record propagates before it starts governing rounds.</summary>
        private const int EffectiveHeightMargin = 2;

        /// <summary>
        /// Builds record head+1 applying the given change, collects majority signatures from the
        /// CURRENT record's casters, appends locally, and broadcasts the finalized record.
        /// Returns false when the record era is active but the rotation could not be completed —
        /// callers must then ABORT the live-set mutation.
        /// </summary>
        public static async Task<bool> ProposeRotationAsync(string changeType, string changedAddress, CasterInfo? addedCaster)
        {
            if (!CasterMembershipStore.RecordEraActive)
                return true; // legacy era — nothing to do

            try
            {
                var head = CasterMembershipStore.GetCurrent();
                if (head == null)
                {
                    CasterLogUtility.Log("MEMBERSHIP: rotation aborted — no head record.", "MEMBERSHIP");
                    return false;
                }

                // Derive the new caster set from the head record (authoritative), not the bag.
                var newSet = head.Casters.Select(c => new CasterInfo { Address = c.Address, PeerIP = c.PeerIP, PublicKey = c.PublicKey }).ToList();
                if (changeType == "Promotion")
                {
                    if (addedCaster == null || string.IsNullOrEmpty(addedCaster.Address))
                        return false;
                    if (!newSet.Any(c => c.Address == addedCaster.Address))
                        newSet.Add(new CasterInfo { Address = addedCaster.Address, PeerIP = (addedCaster.PeerIP ?? "").Replace("::ffff:", ""), PublicKey = addedCaster.PublicKey ?? "" });
                }
                else // Demotion | Departure
                {
                    newSet.RemoveAll(c => c.Address == changedAddress);
                    if (newSet.Count == 0)
                    {
                        CasterLogUtility.Log("MEMBERSHIP: rotation aborted — change would empty the caster set.", "MEMBERSHIP");
                        return false;
                    }
                }

                var candidate = new CasterMembershipRecord
                {
                    RecordSeq = head.RecordSeq + 1,
                    EffectiveFromHeight = Math.Max(head.EffectiveFromHeight + 1, (Globals.LastBlock?.Height ?? 0) + EffectiveHeightMargin),
                    Casters = newSet.OrderBy(c => c.Address, StringComparer.Ordinal).ToList(),
                    PrevRecordHash = head.RecordHash,
                    ChangeType = changeType,
                    ChangedAddress = changedAddress ?? "",
                    Signatures = new List<RecordSignature>()
                };
                candidate.RecordHash = CasterMembershipStore.ComputeRecordHash(candidate);

                // Collect signatures: self first, then the other members of the PREVIOUS set.
                var payload = CasterMembershipStore.CanonicalPayload(candidate);
                var prevSet = head.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
                var need = prevSet.Count / 2 + 1;

                var account = AccountData.GetLocalValidator();
                if (account?.GetPrivKey != null && !string.IsNullOrEmpty(Globals.ValidatorAddress) && prevSet.Contains(Globals.ValidatorAddress))
                {
                    if (!CasterMembershipStore.TryMarkSigned(candidate.RecordSeq, candidate.RecordHash))
                    {
                        CasterLogUtility.Log($"MEMBERSHIP: rotation aborted — already signed a DIFFERENT record at seq {candidate.RecordSeq} (equivocation guard).", "MEMBERSHIP");
                        return false;
                    }
                    var selfSig = SignatureService.CreateSignature(payload, account.GetPrivKey, account.PublicKey);
                    if (selfSig != "ERROR")
                        candidate.Signatures.Add(new RecordSignature { SignerAddress = Globals.ValidatorAddress, Signature = selfSig });
                }

                var signRequest = JsonConvert.SerializeObject(new MembershipSignRequest { Candidate = candidate, ProposerAddress = Globals.ValidatorAddress ?? "" });
                foreach (var member in head.Casters)
                {
                    if (candidate.Signatures.Count >= need) break;
                    if (member.Address == Globals.ValidatorAddress || string.IsNullOrEmpty(member.PeerIP)) continue;
                    try
                    {
                        using var client = Globals.HttpClientFactory.CreateClient();
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var uri = $"http://{member.PeerIP}:{Globals.ValAPIPort}/valapi/validator/SignMembershipRecord";
                        using var content = new StringContent(signRequest, Encoding.UTF8, "application/json");
                        var resp = await client.PostAsync(uri, content, cts.Token);
                        if (!resp.IsSuccessStatusCode) continue;
                        var body = await resp.Content.ReadAsStringAsync();
                        var sig = JsonConvert.DeserializeObject<RecordSignature>(body);
                        if (sig == null || sig.SignerAddress != member.Address) continue;
                        if (!SignatureService.VerifySignature(sig.SignerAddress, payload, sig.Signature)) continue;
                        if (!candidate.Signatures.Any(s => s.SignerAddress == sig.SignerAddress))
                            candidate.Signatures.Add(sig);
                    }
                    catch { /* unreachable member */ }
                }

                if (candidate.Signatures.Count < need)
                {
                    CasterLogUtility.Log($"MEMBERSHIP: rotation ABORTED — {candidate.Signatures.Count}/{need} signatures for seq {candidate.RecordSeq} ({changeType} {changedAddress}).", "MEMBERSHIP");
                    return false;
                }

                if (!CasterMembershipStore.TryAppend(candidate, out var reason))
                {
                    CasterLogUtility.Log($"MEMBERSHIP: rotation append failed — {reason}.", "MEMBERSHIP");
                    return false;
                }

                await BroadcastRecordAsync(candidate);
                ReconcileBlockCastersToRecord(candidate);
                return true;
            }
            catch (Exception ex)
            {
                CasterLogUtility.Log($"MEMBERSHIP: rotation error — {ex.Message}", "MEMBERSHIP");
                return false;
            }
        }

        /// <summary>Best-effort push of a finalized record to committee members + known validator peers.</summary>
        public static async Task BroadcastRecordAsync(CasterMembershipRecord record)
        {
            var json = JsonConvert.SerializeObject(record);
            var targets = record.Casters.Select(c => c.PeerIP)
                .Concat(Globals.ValidatorNodes.Values.Select(n => n.NodeIP))
                .Where(ip => !string.IsNullOrEmpty(ip))
                .Select(ip => ip!.Replace("::ffff:", ""))
                .Distinct()
                .Take(20)
                .ToList();

            var tasks = targets.Select(async ip =>
            {
                try
                {
                    using var client = Globals.HttpClientFactory.CreateClient();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var uri = $"http://{ip}:{Globals.ValAPIPort}/valapi/validator/AnnounceMembershipRecord";
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await client.PostAsync(uri, content, cts.Token);
                }
                catch { }
            });
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Reconciles the live connectivity pool (Globals.BlockCasters) to the given record:
        /// removes entries not in the record, adds record members via the atomic capped add,
        /// and updates IsBlockCaster for self.
        /// </summary>
        public static void ReconcileBlockCastersToRecord(CasterMembershipRecord record)
        {
            var recordAddrs = record.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);

            var keep = Globals.BlockCasters.ToList().Where(p => !string.IsNullOrEmpty(p.ValidatorAddress) && recordAddrs.Contains(p.ValidatorAddress!)).ToList();
            var nBag = new System.Collections.Concurrent.ConcurrentBag<Peers>();
            keep.ForEach(x => nBag.Add(x));
            Globals.BlockCasters = nBag;

            foreach (var member in record.Casters)
            {
                if (Globals.BlockCasters.Any(p => p.ValidatorAddress == member.Address)) continue;
                CasterDiscoveryService.AddBlockCasterIfRoomAndUnique(new Peers
                {
                    IsIncoming = false,
                    IsOutgoing = true,
                    PeerIP = member.PeerIP,
                    IsValidator = true,
                    ValidatorAddress = member.Address,
                    ValidatorPublicKey = member.PublicKey
                });
            }

            Globals.SyncKnownCastersFromBlockCasters();

            var selfInRecord = !string.IsNullOrEmpty(Globals.ValidatorAddress) && recordAddrs.Contains(Globals.ValidatorAddress);
            if (Globals.IsBlockCaster && !selfInRecord)
            {
                Globals.IsBlockCaster = false;
                CasterLogUtility.Log("MEMBERSHIP: self not in current record — standing down to validator.", "MEMBERSHIP");
            }
        }

        /// <summary>
        /// Endpoint-side signing decision: verifies the candidate derives from OUR head, that we
        /// are a member of the previous set, and that we have not signed a different record at
        /// this seq. Returns our signature or null.
        /// Note: majority signatures are not required at signing time (they're being collected);
        /// ValidateSuccessor's signature check is enforced at append, not here.
        /// </summary>
        public static RecordSignature? HandleSignRequest(MembershipSignRequest request)
        {
            if (request?.Candidate == null)
                return null;

            // Wave 6: GENESIS signing — only a hardcoded seed, only while in an active bootstrap
            // agreement, and only for the exact boundary that agreement produced. This is how the
            // restart event itself arms the record era with no configured height.
            if (request.Candidate.RecordSeq == 0)
                return HandleGenesisSignRequest(request);

            var head = CasterMembershipStore.GetCurrent();
            if (head == null) return null;

            var candidate = request.Candidate;

            // Structural derivation checks (signature-quorum check deliberately excluded here).
            if (candidate.RecordSeq != head.RecordSeq + 1) return null;
            if (!string.Equals(candidate.PrevRecordHash, head.RecordHash, StringComparison.OrdinalIgnoreCase)) return null;
            if (candidate.EffectiveFromHeight <= head.EffectiveFromHeight) return null;
            if (candidate.Casters == null || candidate.Casters.Count == 0) return null;
            if (CasterMembershipStore.ComputeRecordHash(candidate) != candidate.RecordHash) return null;

            // The change must be a single add/remove consistent with ChangeType/ChangedAddress.
            var headSet = head.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
            var candSet = candidate.Casters.Select(c => c.Address).ToHashSet(StringComparer.Ordinal);
            var added = candSet.Except(headSet).ToList();
            var removed = headSet.Except(candSet).ToList();
            var validChange = candidate.ChangeType switch
            {
                "Promotion" => added.Count == 1 && removed.Count == 0 && added[0] == candidate.ChangedAddress,
                "Demotion" or "Departure" => added.Count == 0 && removed.Count == 1 && removed[0] == candidate.ChangedAddress,
                _ => false
            };
            if (!validChange) return null;

            // We must be a member of the PREVIOUS set to be an eligible signer.
            var account = AccountData.GetLocalValidator();
            if (account?.GetPrivKey == null || string.IsNullOrEmpty(Globals.ValidatorAddress)) return null;
            if (!headSet.Contains(Globals.ValidatorAddress)) return null;

            // Equivocation guard: never sign two different records at the same seq.
            if (!CasterMembershipStore.TryMarkSigned(candidate.RecordSeq, candidate.RecordHash))
            {
                CasterLogUtility.Log($"MEMBERSHIP: REFUSED double-sign at seq {candidate.RecordSeq} (different hash). Proposer={request.ProposerAddress}", "MEMBERSHIP");
                return null;
            }

            var payload = CasterMembershipStore.CanonicalPayload(candidate);
            var sig = SignatureService.CreateSignature(payload, account.GetPrivKey, account.PublicKey);
            if (sig == "ERROR") return null;

            CasterLogUtility.Log($"MEMBERSHIP: signed rotation seq={candidate.RecordSeq} ({candidate.ChangeType} {candidate.ChangedAddress}) for proposer {request.ProposerAddress}.", "MEMBERSHIP");
            return new RecordSignature { SignerAddress = Globals.ValidatorAddress, Signature = sig };
        }

        private static RecordSignature? HandleGenesisSignRequest(MembershipSignRequest request)
        {
            var candidate = request.Candidate!;

            if (CasterMembershipStore.GetCurrent() != null)
                return null; // an era already exists — never co-sign a second genesis
            if (!Globals.IsLocalBootstrapCaster || string.IsNullOrEmpty(Globals.ValidatorAddress))
                return null;
            if (BootstrapCoordinationService.State != BootstrapCoordinationService.BootstrapState.Agreed)
                return null;
            if (candidate.EffectiveFromHeight != BootstrapCoordinationService.AgreedHeight + 1)
                return null; // must match OUR agreement's boundary exactly

            // Structural checks: must be the deterministic genesis for this network + boundary.
            var expected = CasterMembershipStore.BuildGenesisRecord(candidate.EffectiveFromHeight);
            if (candidate.RecordHash != expected.RecordHash || CasterMembershipStore.ComputeRecordHash(candidate) != expected.RecordHash)
                return null;

            var account = AccountData.GetLocalValidator();
            if (account?.GetPrivKey == null)
                return null;

            if (!CasterMembershipStore.TryMarkSigned(0, candidate.RecordHash))
            {
                CasterLogUtility.Log($"MEMBERSHIP: REFUSED genesis double-sign (different hash at seq 0). Proposer={request.ProposerAddress}", "MEMBERSHIP");
                return null;
            }

            var payload = CasterMembershipStore.CanonicalPayload(candidate);
            var sig = SignatureService.CreateSignature(payload, account.GetPrivKey, account.PublicKey);
            if (sig == "ERROR") return null;

            CasterLogUtility.Log($"MEMBERSHIP: co-signed GENESIS (boundary {candidate.EffectiveFromHeight}) for proposer {request.ProposerAddress}.", "MEMBERSHIP");
            return new RecordSignature { SignerAddress = Globals.ValidatorAddress, Signature = sig };
        }

        /// <summary>
        /// Departure rotations need a proposer among the REMAINING casters (the departing node
        /// is shutting down). Deterministic: the lexicographically-lowest remaining address.
        /// </summary>
        public static bool IsSelfDepartureProposer(string departingAddress)
        {
            var head = CasterMembershipStore.GetCurrent();
            if (head == null || string.IsNullOrEmpty(Globals.ValidatorAddress)) return false;
            var remaining = head.Casters.Select(c => c.Address)
                .Where(a => a != departingAddress)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();
            return remaining.FirstOrDefault() == Globals.ValidatorAddress;
        }
    }
}
