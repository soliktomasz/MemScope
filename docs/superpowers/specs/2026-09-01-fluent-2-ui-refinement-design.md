# Fluent 2 UI Refinement Design

## Goal

Refine MemScope into a more cohesive Fluent 2 desktop experience while preserving its existing workflows, data density, accessibility, and cross-platform behavior.

## Design direction

MemScope remains a focused diagnostics workbench for .NET developers and performance engineers. The visual language is calm, precise, and information-first, using Avalonia's native `FluentTheme` as the governing design system.

The design dials are `DESIGN_VARIANCE: 4`, `MOTION_INTENSITY: 3`, and `VISUAL_DENSITY: 7`. This is a preserve-mode redesign. It retains the current information architecture, system light and dark themes, blue accent, compact data tables, and all existing commands and bindings.

## Visual system

- Centralize semantic colors, typography, spacing, corner radii, and shared component styles in `App.axaml`.
- Use a cool neutral palette with one Fluent blue accent across both themes.
- Use a documented Fluent radius hierarchy: 8 px for containing surfaces and 4 px for controls and compact interactive rows.
- Prefer low-contrast layer fills and separators over heavy borders or repeated card elevation.
- Keep system UI typography for native platform fit and retain a monospace stack for addresses, counts, and memory values.
- Use restrained native transitions and control feedback only. No decorative animation or custom animation dependency is introduced.

## Application shell

The start surface keeps its existing title, three primary workflows, process picker, and recent sessions. The hierarchy changes from a loose vertical page into a deliberate Fluent workbench:

- A compact page header establishes product identity and supporting copy.
- The three workflow commands form a clear command bar, with Attach to Process as the primary action.
- The process picker becomes the main working surface when open, with its title, refresh action, progress, table, and confirmation action grouped together.
- Recent sessions use a quieter secondary surface with improved row hover, selection, spacing, and path hierarchy.

No navigation labels, command names, or workflow ordering change.

## Investigation surfaces

Live diagnostics, snapshot analysis, and comparison use the same structural rhythm:

- A shared page header style for title, context, status, and commands.
- Compact command buttons with consistent primary, secondary, and subtle treatments.
- Section headers that use sentence case and typographic hierarchy instead of repeated all-caps labels where practical.
- Data tables with clearer headers, lighter grid treatment, consistent row height, stronger selection, and visible focus.
- Analysis panes use subtle surface fills and separators to clarify ownership without turning every region into a card.
- Filters align as a compact toolbar and retain their current bindings and behavior.
- Metrics remain visually prominent, with monospace values and subdued supporting labels.

## States and accessibility

Existing loading, empty, error, disconnected, and populated states remain in place. Their presentation is standardized through shared styles so the affected region remains clear and layout does not jump.

- Preserve automation names and live-region behavior.
- Preserve logical tab order, keyboard selection, context menus, and visible focus cues.
- Use color only as reinforcement. Status continues to include readable text.
- Maintain readable contrast in both theme variants.
- Keep native Fluent progress controls so reduced-motion preferences remain respected without custom handling.

## Implementation boundaries

- Primary changes are limited to `App.axaml`, `MainWindow.axaml`, and the existing view XAML files.
- Code-behind, view models, services, contracts, navigation, data flow, and persistence remain unchanged unless a markup limitation makes a tiny app-layer adjustment unavoidable.
- No new package or icon library is required.
- The existing uncommitted `SnapshotView.axaml` progress-layout change is preserved and incorporated rather than overwritten.
- Unrelated working-tree files remain untouched.

## Verification

This pass is visual and structural, with no intended behavior changes. Verification consists of:

- Building `MemoryProfiler.sln` to compile all XAML and bindings.
- Reviewing the resulting diff for accidental binding, command, automation, or visibility changes.
- Running only a narrowly relevant existing test if the build exposes a behavioral concern.

The full test suite and new tests are intentionally omitted because the task changes styling and layout rather than application logic.

## Scope limits

No new profiler capabilities, navigation redesign, custom charting, new icons, animation framework, data-table replacement, theme toggle, or marketing surface is included.
