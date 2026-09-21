using Xunit;

// The DbContextSequential and GlobalCasterState collections each serialize internally, but xUnit
// still runs the two collections against each other in parallel — and both reach the SAME static
// LiteDB handles in VerifiedXCore.Data.DbContext. A DbContextSequential class repoints
// Globals.CustomPath and opens/closes those handles while a GlobalCasterState class is mid-query
// (CasterMembershipStore.GetCurrent), which surfaces as "Database lock timeout when entering in
// transaction mode after 00:01:00" on whichever caster test lost the race. It was latent — the
// overlap window simply had to be long enough — and grew reliable as the DB-backed suites did.
//
// Disabling cross-collection parallelism is the only assembly-wide fix: per-collection
// DisableParallelization cannot express "these two collections must not overlap".
[assembly: CollectionBehavior(DisableTestParallelization = true)]
