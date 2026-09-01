# Profiler Error Handling Design

## Goal

Expected diagnostics and analysis failures must become clear, actionable UI states instead of crashes or raw exception messages.

## Error model

The app owns a small `ProfilerError` presentation model with a category, title, explanation, and technical details. `ProfilerErrorFactory` maps exceptions in an operation context to the required categories: process exited, access denied, unsupported runtime, unable to attach, dump capture failed, dump corrupted, CLR runtime not found, snapshot incompatible, insufficient disk space, and analysis cancelled.

Primary copy is stable and user-oriented. The exception type, message, and inner-exception chain are retained only in technical details. Unknown failures use the operation's safe fallback category.

## Integration

Top-level attach, capture, snapshot loading, and comparison operations publish `ProfilerError` instances. Existing nested analysis panes use the same presentation model for unexpected analysis failures so no primary error surface concatenates `Exception.Message`.

Long-running view-model entry points accept an optional `CancellationToken`, link it with lifecycle cancellation, and distinguish requested cancellation from failure. Cancellation caused by closing or superseding work remains silent; an analysis cancellation initiated at the active operation is represented as `Analysis cancelled`.

## UI

A reusable native Avalonia `ErrorDetailsView` renders the title and explanation in the existing semantic error colors. An `Expander` labeled `Technical details` reveals selectable wrapped details. The control preserves FluentTheme, keyboard focus, screen-reader announcements, the existing four-pixel radius, and the current light/dark token system. No animation or additional dependency is introduced.

Design read: native Avalonia diagnostics workbench for developers and performance engineers, with a precise, serious, JetBrains-like language leaning on Avalonia FluentTheme and semantic tokens. Dials: `DESIGN_VARIANCE: 4`, `MOTION_INTENSITY: 2`, `VISUAL_DENSITY: 8`.

## Testing

Unit tests cover every required category, safe primary copy, technical detail retention, disk-full recognition, and view-model publication without throwing. Existing cancellation and lifecycle tests remain green. The app project and full solution test suite provide integration verification.
