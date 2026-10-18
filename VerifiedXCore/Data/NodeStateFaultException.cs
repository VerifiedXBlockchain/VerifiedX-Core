namespace VerifiedXCore.Data
{
    /// <summary>
    /// A block-start or block-end step found this node's own state missing something every node that applied the chain
    /// holds (state built by an older release, a store this node failed to write). It is a fault of this node, like a
    /// storage error, so <see cref="StateData.IsNodeLocalFault"/> counts it and the block is flagged as not applied here;
    /// a throw caused by a block's data is the same on every node and does not flag it.
    /// </summary>
    public sealed class NodeStateFaultException : InvalidOperationException
    {
        public NodeStateFaultException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
