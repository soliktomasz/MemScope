using Xunit;

namespace MemoryProfiler.Analysis.Tests;

// Acceptance tests that capture live processes swap the ambient TMPDIR so the
// diagnostics transport socket lands in a short path. TMPDIR is process-wide,
// so every test that mutates it must run serially against all other tests.
[CollectionDefinition("Live diagnostics", DisableParallelization = true)]
public sealed class LiveDiagnosticsCollectionDefinition
{
}
