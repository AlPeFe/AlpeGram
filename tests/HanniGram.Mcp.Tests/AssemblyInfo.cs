using Xunit;

// The MCP suite shares the process-wide SQLite connection pool (a harness drops
// idle pooled handles so its temp files can be deleted), so it must not run its
// own tests in parallel either.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
