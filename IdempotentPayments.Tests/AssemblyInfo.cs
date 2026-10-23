// Integration test classes share one database (payment_test). The sweep settles
// EVERY stale row it finds, so test classes running in parallel would settle each
// other's planted rows. Run test classes one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
