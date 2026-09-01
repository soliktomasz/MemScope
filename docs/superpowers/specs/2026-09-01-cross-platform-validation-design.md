# Cross-Platform Validation Design

## Goal

Continuously validate MemScope on Windows, macOS, and Linux, and state the practical platform limits of live diagnostics and dump analysis.

## CI workflow

Add `.github/workflows/ci.yml`. It runs for pushes to `main` and pull requests targeting `main`, with one matrix job on `windows-latest`, `macos-latest`, and `ubuntu-latest`.

Each job checks out the repository, installs the .NET 10 SDK, then runs these explicit Release commands against `MemoryProfiler.sln`:

1. `dotnet restore`
2. `dotnet build --configuration Release --no-restore`
3. `dotnet test --configuration Release --no-build`

The matrix does not use fail-fast, so a platform failure does not hide results from the others. NuGet caching is unnecessary for this initial workflow.

## Platform integration coverage

The existing acceptance tests already exercise process attachment, EventPipe metrics, dump capture, dump loading, object references, GC roots, and retained-size analysis with real helper processes. The full solution test command will run those tests independently on every matrix runner. Platform-specific branching will be added only if CI exposes a genuine OS-specific requirement.

## Documentation

Extend `README.md` with a platform-support section. Windows, macOS, and Linux support the same core workflow when the current user can access a compatible .NET target process. Explicit limitations are same-user/permission requirements, same-machine attachment, platform-specific dump formats, and the expectation that dumps are analyzed on the operating-system and architecture family that produced them.

Manual GUI validation remains necessary for native file pickers and end-to-end desktop interaction; automated CI validates the underlying services and view models without launching the desktop UI.

## Verification

Run restore, Release build, and Release tests locally. Validate the workflow structure by inspection, then rely on GitHub Actions for the two operating systems unavailable on the development machine.
