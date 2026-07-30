namespace VerifiedXCore.Models
{
    /// <summary>
    /// Wave 3 (split-brain root fix): height-anchored, hash-chained, majority-signed record of
    /// caster-set membership. Record N is valid only when it extends record N-1 (seq + PrevRecordHash)
    /// and carries signatures from a majority of record N-1's caster set — so any single valid
    /// response is cryptographically trustworthy and disjoint local quorums cannot form.
    /// Record 0 (genesis) is minted by the seeds at the coordinated restart's bootstrap
    /// agreement (EffectiveFromHeight = agreed height + 1) and must carry ≥2 signatures from
    /// the hardcoded seed allowlist — the same trust root as the bootstrap agreement itself.
    /// </summary>
    public class CasterMembershipRecord
    {
        /// <summary>LiteDB id; strictly increasing, no gaps. Genesis = 0.</summary>
        public long RecordSeq { get; set; }

        /// <summary>The record governs blocks/rounds with Height >= this.</summary>
        public long EffectiveFromHeight { get; set; }

        /// <summary>The caster set, sorted by Address ordinal ascending.</summary>
        public List<CasterInfo> Casters { get; set; } = new();

        /// <summary>RecordHash of record N-1; "GENESIS" for seq 0.</summary>
        public string PrevRecordHash { get; set; } = "";

        /// <summary>SHA256 hex (lowercase) of the canonical FormatCasterMembershipV1 payload.</summary>
        public string RecordHash { get; set; } = "";

        /// <summary>Genesis | Promotion | Demotion | Departure</summary>
        public string ChangeType { get; set; } = "";

        /// <summary>The address added or removed by this rotation ("" for genesis).</summary>
        public string ChangedAddress { get; set; } = "";

        /// <summary>Signatures over the canonical payload from members of the PREVIOUS record's set.</summary>
        public List<RecordSignature> Signatures { get; set; } = new();
    }

    /// <summary>One rotation signature: signer must be a member of the previous record's caster set.</summary>
    public class RecordSignature
    {
        public string SignerAddress { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    /// <summary>Wave 3: request body for SignMembershipRecord (candidate record awaiting signatures).</summary>
    public class MembershipSignRequest
    {
        public CasterMembershipRecord? Candidate { get; set; }
        public string ProposerAddress { get; set; } = "";
    }

    /// <summary>Wave 3: response payload of GetMembershipRecord — the head seq plus requested records.</summary>
    public class MembershipRecordResponse
    {
        public long HeadSeq { get; set; } = -1;
        public List<CasterMembershipRecord> Records { get; set; } = new();
    }
}
