# Session Storage Design

## Goal

Persist local profiler session metadata so restarting MemScope restores Recent Sessions without copying memory dumps or introducing any account or cloud service.

## Storage Model

The Storage project owns an immutable `SessionCatalog` with three collections:

- `RecentDump`: dump path, process name, process ID when known, runtime version when known, capture or discovery timestamp, and nullable object-count and heap-size metadata until analysis succeeds.
- `RecentInvestigation`: dump path, process name when known, and the last-opened timestamp.
- `ComparisonPair`: before and after dump paths plus the last-compared timestamp.

Paths reference existing dump files. MemScope never copies dump contents as part of session persistence. Entries are deduplicated by path, or by the ordered before/after path pair for comparisons, sorted newest first, and capped at 20 entries per collection. Path equality uses `OrdinalIgnoreCase` on Windows and `Ordinal` on macOS and Linux.

## Repository

`ISessionRepository` exposes cancellable asynchronous load and save operations for the complete catalog. `JsonSessionRepository` stores `sessions.json` beneath a `MemScope` directory rooted at `Environment.SpecialFolder.LocalApplicationData`, allowing .NET to select the platform-appropriate application-data location.

Writes create the parent directory when needed, serialize to a temporary file in that directory, and atomically replace the catalog file. Cancellation or a write failure leaves the previous catalog intact. Missing files load as an empty catalog. Invalid JSON and other read failures are reported to the caller rather than silently discarding stored data. Tests can provide an explicit catalog path and never touch real user data.

## Application Integration

`StartViewModel` loads the catalog asynchronously during application startup and publishes Recent Sessions through dedicated row view models. It serializes catalog mutations so overlapping capture, analysis, and comparison completions cannot lose updates.

The existing workflows report successful milestones through small callbacks:

- A completed dump capture records a recent dump immediately, using the live process identity and captured path.
- A successfully loaded snapshot enriches the recent dump with `HeapSnapshotInfo` and records a recent investigation.
- A successfully completed comparison records its ordered path pair.

Persistence failures are nonfatal to diagnostics and analysis. The start screen exposes a concise storage error while retaining any in-memory history available for the current run.

Selecting a recent snapshot opens its existing path through the normal snapshot-loading flow. Selecting a comparison opens the comparison workspace with the stored before and after paths and starts the normal cancellable comparison flow. A missing or unreadable dump produces the existing analysis error state and does not remove the stored reference automatically.

## Recent Sessions UI

Reading this as: a native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious developer-tool language, leaning toward Avalonia FluentTheme plus semantic resources.

The design uses `DESIGN_VARIANCE: 4`, `MOTION_INTENSITY: 2`, and `VISUAL_DENSITY: 8`. The existing start-screen structure, typography, accent, and shape system remain unchanged. Recent Sessions becomes a compact list separated by the existing border treatment rather than a set of decorative cards. Each row shows a functional title, timestamp, referenced path, and an Open action with keyboard focus and an accessible name.

The section provides loading, empty, populated, error, and disabled states. No animation, new icon family, web dependency, or custom control is introduced. Dates and sizes use `CultureInfo.CurrentCulture`.

## Testing

- Storage tests cover missing files, JSON round-trips, cancellation, corrupt JSON, atomic replacement, deduplication, ordering, limits, and default path construction.
- App tests cover startup restoration, successful capture/snapshot/comparison recording, persistence failure isolation, row ordering, and reopening snapshot and comparison entries.
- The solution build validates compiled Avalonia bindings and project references.
- The full solution test suite provides regression coverage, including live diagnostics acceptance tests.
