# UX Polish Design

## Goal

Polish the existing profiler investigation workflow so dense memory data is easier to navigate, copy, sort, and understand while long-running analysis remains responsive and cancellable.

## Design direction

MemScope remains a native Avalonia diagnostics workbench for developers and performance engineers. The visual language is precise and serious, close to JetBrains tooling while retaining Avalonia FluentTheme. The design dials are `DESIGN_VARIANCE: 4`, `MOTION_INTENSITY: 2`, and `VISUAL_DENSITY: 8`.

This is a targeted evolution, not a redesign. Preserve the current information architecture, automatic light/dark theme, blue semantic accent, 4 px radius, compact tables, and existing navigation behavior. Avoid decorative motion, new web dependencies, and generic card-heavy layouts.

## Interaction model

- Every investigation table supports keyboard focus, arrow-key selection, column resizing, and column sorting.
- Context menus expose only actions valid for the selected row. Type rows copy the type name. Object and reference rows copy the object address. GC-root rows copy the complete displayed root path.
- Copy actions use an injected clipboard abstraction so view-model behavior is testable and platform integration stays in the app layer.
- Addresses are copied in the same canonical hexadecimal form shown in the UI.
- Existing back and forward investigation navigation remains unchanged.

## Formatting

- Counts use `CultureInfo.CurrentCulture` thousands grouping.
- Memory values use binary units with a 1024 divisor and the compact examples required by the issue: `824 B`, `14.2 KB`, `48.7 MB`, and `1.31 GB`.
- Byte formatting uses enough precision to remain useful: no decimals for bytes, one decimal for KB and MB, and two decimals for GB and larger units. Trailing zeroes are suppressed.
- Signed sizes retain their explicit sign and use the same byte formatter.

## Loading and progress

- Short refreshes keep restrained inline progress indicators or skeleton rows that preserve layout.
- Snapshot loading, comparison, retained-size analysis, object/reference loading, and GC-root discovery expose clear loading text.
- Operations that already own cancellable work expose a visible Cancel action in a progress overlay. Cancellation reuses the existing cancellation sources and version guards rather than introducing parallel state.
- Overlays block only the affected investigation surface, not unrelated navigation or window controls.
- Reduced-motion behavior is naturally honored because indicators use native Fluent controls without decorative animation.

## Complete states

Each major surface has explicit loading, empty, error, populated, selected, and disabled states. Empty-state copy explains the next useful action without marketing language. Error details remain contextual and existing recovery actions are preserved.

## Avalonia structure

- Move shared table, focus, action, overlay, and empty-state styling into application resources where it prevents duplication.
- Use native Avalonia controls and virtualization. Prefer `DataGrid` where resizing and sorting are built in; keep existing virtualized list behavior when conversion would regress performance or selection semantics.
- Add small reusable app-layer services or view models only where clipboard and progress behavior need a clean test boundary.
- Keep CPU-bound work off the UI thread and marshal observable state through `UiDispatcher`.

## Accessibility

- Preserve visible focus cues and logical tab order.
- Give loading regions, menus, copy commands, and cancel buttons useful automation names.
- Do not encode state using color alone. Maintain readable text and control contrast in both theme variants.
- Keyboard and pointer users receive the same available actions.

## Testing

- Unit-test byte, signed-byte, count, and address formatting with fixed cultures and boundary values.
- Unit-test clipboard commands, command enablement, and cancellation state before production changes.
- Extend view-model tests for loading, empty, populated, cancellation, and superseded-result behavior.
- Add narrow Avalonia control tests for table sorting/resizing configuration, context-menu bindings, focusability, and automation names where the behavior cannot be proven at the view-model layer.
- Run the full solution build and test suite after focused tests pass.

## Scope limits

No new profiling or analysis capabilities, navigation restructuring, third-party UI framework, custom animation system, persistence schema change, or marketing-site work is included.
