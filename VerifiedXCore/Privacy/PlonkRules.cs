namespace VerifiedXCore.Privacy
{
    /// <summary>
    /// The rules a private transaction's proofs are judged under, fixed by the height of the block that carries it
    /// (<see cref="PlonkProofVerifier.RulesAt"/>). Each value is the native verifier it calls; a proof made under one set
    /// passes no other.
    /// </summary>
    public enum PlonkRules : byte
    {
        /// <summary>Below Globals.PlonkCurrentRulesHeight: <c>plonk_verify</c>, the rules proofs were mined under then.</summary>
        Original = 0,
        /// <summary>From Globals.PlonkCurrentRulesHeight: <c>plonk_verify2</c>, made by <c>plonk_prove_*</c>.</summary>
        Current = 1,
        /// <summary>From Globals.PlonkKeyBoundRulesHeight: <c>plonk_verify3</c>, made by <c>plonk_prove_*3</c>; the transcript binds the whole verifier key.</summary>
        KeyBound = 2,
    }
}
