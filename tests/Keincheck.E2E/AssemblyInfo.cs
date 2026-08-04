using Xunit;

// One hub per user per machine, enforced by the hub's own single-instance mutex, a
// fixed control pipe, and a fixed port 3100. Running these in parallel would have
// them fighting over all three.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
