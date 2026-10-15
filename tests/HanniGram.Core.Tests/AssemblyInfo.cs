using Xunit;

// TempDatabase clears the process-wide Microsoft.Data.Sqlite connection pool on
// dispose so its temp WAL/SHM files can be removed. That pool is shared by every
// test in the process, and ClearAllPools() disposes pooled connections that a
// concurrently running test may be renting, which surfaced intermittently as
// ObjectDisposedException in an unrelated test. The suite manipulates
// process-global state, so its classes must not run in parallel. The suite is
// tiny (sub-second), so serializing costs nothing worth measuring.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
