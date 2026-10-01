namespace VerifiedXCore.Models
{
    /// <summary>A restarted committee member asks to take its seat back. Signed by the member over
    /// <c>RESUME|address|recordSeq|recordHash|timestamp</c>.</summary>
    public class CasterResumeRequest
    {
        public string Address { get; set; } = "";
        public long RecordSeq { get; set; }
        public string RecordHash { get; set; } = "";
        public long Timestamp { get; set; }
        public string Signature { get; set; } = "";
    }

    /// <summary>A committee member's answer to a resume request. An approval is signed over
    /// <c>RESUME-OK|address|recordSeq|recordHash|requestTimestamp</c>; a refusal carries the reason and the
    /// signer's head sequence so a requester holding an older record can catch up.</summary>
    public class CasterResumeApproval
    {
        public string SignerAddress { get; set; } = "";
        public string Address { get; set; } = "";
        public long RecordSeq { get; set; }
        public string RecordHash { get; set; } = "";
        public long Timestamp { get; set; }
        public bool Approve { get; set; }
        public string Reason { get; set; } = "";
        public long HeadSeq { get; set; } = -1;
        public string Signature { get; set; } = "";
    }

    /// <summary>Sent by a member that resumed: its request plus the majority of approvals that let it back.</summary>
    public class CasterResumedNotice
    {
        public CasterResumeRequest? Request { get; set; }
        public List<CasterResumeApproval> Approvals { get; set; } = new();
    }

    /// <summary>/maintenance: the member is about to restart and asks for the longer grace period. Signed over
    /// <c>MAINT|address|timestamp</c>.</summary>
    public class CasterMaintenanceNotice
    {
        public string Address { get; set; } = "";
        public long Timestamp { get; set; }
        public string Signature { get; set; } = "";
    }
}
