# Fluent 2 UI Refinement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refine MemScope's existing Avalonia UI into a cohesive Fluent 2 desktop workbench without changing application behavior.

**Architecture:** Keep Avalonia's native `FluentTheme` and move shared visual decisions into semantic application resources and selectors in `App.axaml`. Recompose only XAML layout and styling in the existing shell and views, preserving every binding, command, visibility condition, automation property, and code-behind event.

**Tech Stack:** .NET 10, C#, Avalonia 12.1.1, AXAML, Avalonia FluentTheme, Avalonia DataGrid

**Spec:** `docs/superpowers/specs/2026-09-01-fluent-2-ui-refinement-design.md`

## Global Constraints

- Preserve the current information architecture, system light and dark themes, blue accent, compact data tables, and all existing commands and bindings.
- Use a cool neutral palette with one Fluent blue accent across both themes.
- Use 8 px corner radii for containing surfaces and 4 px radii for controls and compact interactive rows.
- Use restrained native transitions and control feedback only. Do not introduce decorative animation or a custom animation dependency.
- Do not add packages, icons, profiler capabilities, navigation changes, custom charting, a theme toggle, or marketing UI.
- Preserve the existing uncommitted `src/MemoryProfiler.App/Views/SnapshotView.axaml` progress-layout change.
- Do not modify code-behind, view models, services, contracts, navigation, data flow, or persistence.
- Skip new tests and the full test suite. Build the solution and inspect the XAML diff because this is a visual-only change.
- Prefix every shell command with `rtk`.

---

### Task 1: Shared Fluent 2 visual system

**Files:**
- Modify: `src/MemoryProfiler.App/App.axaml`

**Interfaces:**
- Consumes: Avalonia `FluentTheme`, theme dictionaries, existing `App*Brush` keys
- Produces: shared resource keys and style classes consumed by all four UI surfaces

- [ ] **Step 1: Expand semantic theme resources**

Keep every existing resource key so current views remain valid. Add the following shared resources to the root dictionary and provide light and dark brush values for the new brush keys:

```xml
<CornerRadius x:Key="AppControlCornerRadius">4</CornerRadius>
<CornerRadius x:Key="AppSurfaceCornerRadius">8</CornerRadius>
<Thickness x:Key="AppPageMargin">32,26,32,28</Thickness>
<FontFamily x:Key="AppMonoFontFamily">Cascadia Mono, Menlo, Consolas</FontFamily>
```

Add semantic brushes for raised surface, control hover, selection, and status treatment. Light values should remain in the existing cool-neutral family and dark values in the existing charcoal family. Use the existing blue accent rather than adding another accent hue.

- [ ] **Step 2: Add global typography and component selectors**

Move duplicated styles from individual views into application-level selectors using these class names:

```xml
<Style Selector="TextBlock.page-title" />
<Style Selector="TextBlock.view-title" />
<Style Selector="TextBlock.section-title" />
<Style Selector="TextBlock.section-label" />
<Style Selector="TextBlock.secondary" />
<Style Selector="TextBlock.mono-data" />
<Style Selector="TextBlock.metric-primary" />
<Style Selector="TextBlock.metric-value" />
<Style Selector="Button.app-button" />
<Style Selector="Button.primary" />
<Style Selector="Button.subtle" />
<Style Selector="Border.surface" />
<Style Selector="Border.subtle-surface" />
<Style Selector="Border.empty-state" />
<Style Selector="Border.skeleton" />
```

Set normal command buttons to a 34 px minimum height with 14 by 6 padding, primary buttons to the existing accent brush, and surfaces to the 8 px radius. Keep typography compact: 28 px page title, 24 px investigation title, 16 px section title, 12 px supporting labels, and 13 px default secondary copy.

- [ ] **Step 3: Refine global DataGrid styling**

Retain sorting, resizing, virtualization, and single selection. Change only presentation:

```xml
<Style Selector="DataGrid">
  <Setter Property="Background" Value="Transparent" />
  <Setter Property="BorderBrush" Value="{DynamicResource AppBorderBrush}" />
  <Setter Property="BorderThickness" Value="1" />
  <Setter Property="ColumnHeaderHeight" Value="36" />
  <Setter Property="RowHeight" Value="34" />
  <Setter Property="GridLinesVisibility" Value="Horizontal" />
</Style>
```

Use semantic selection and hover brushes where Avalonia DataGrid selectors support them. Do not replace the control template.

- [ ] **Step 4: Compile the shared resources**

Run:

```bash
rtk dotnet build MemoryProfiler.sln
```

Expected: build succeeds with zero XAML resource-resolution errors.

- [ ] **Step 5: Commit the shared system**

```bash
rtk git add src/MemoryProfiler.App/App.axaml
rtk git commit -m "style: establish shared Fluent 2 resources"
```

---

### Task 2: Application start surface

**Files:**
- Modify: `src/MemoryProfiler.App/MainWindow.axaml`

**Interfaces:**
- Consumes: shared `App*` resources and style classes from Task 1
- Produces: unchanged `StartViewModel` binding surface with refined layout and hierarchy

- [ ] **Step 1: Remove duplicated window-local visual styles**

Delete local selectors now provided by `App.axaml`. Keep only selectors unique to the process and recent-session lists. Rename button class usage to the shared `app-button`, `primary`, and `subtle` classes without changing commands or content.

- [ ] **Step 2: Recompose the header and workflow command bar**

Keep the start layout in the existing `IsStartVisible` grid. Use a compact header surface with the current title and description, followed by a single-line command bar. Keep Attach to Process first and primary; keep Open Dump and Compare Snapshots as secondary commands. Preserve the three existing command bindings exactly.

- [ ] **Step 3: Refine the process picker work surface**

Wrap the existing process picker contents in a `Border` using the shared `surface` style. Use 20 px internal padding and preserve the current row structure, loading progress, error view, DataGrid bindings, empty state, refresh command, selection binding, and Start profiling command. Apply the shared empty-state treatment without adding a decorative icon.

- [ ] **Step 4: Refine recent-session hierarchy**

Use a shared subtle surface and retain the current loading, error, empty, and populated states. Keep title, timestamp, details, path, tooltip, automation name, and Open command unchanged. Reduce visual noise by using one separator between rows and subdued path text.

- [ ] **Step 5: Compile the shell**

Run:

```bash
rtk dotnet build MemoryProfiler.sln
```

Expected: build succeeds and compiled bindings for `StartViewModel` remain valid.

- [ ] **Step 6: Review binding preservation**

Run:

```bash
rtk git diff -- src/MemoryProfiler.App/MainWindow.axaml
```

Expected: all existing `Command`, `ItemsSource`, `SelectedItem`, `IsVisible`, and `DataContext` expressions are still present.

- [ ] **Step 7: Commit the start surface**

```bash
rtk git add src/MemoryProfiler.App/MainWindow.axaml
rtk git commit -m "style: refine Fluent start surface"
```

---

### Task 3: Live diagnostics and comparison surfaces

**Files:**
- Modify: `src/MemoryProfiler.App/Views/LiveSessionView.axaml`
- Modify: `src/MemoryProfiler.App/Views/ComparisonView.axaml`

**Interfaces:**
- Consumes: shared visual resources from Task 1
- Produces: unchanged `LiveSessionViewModel` and `ComparisonViewModel` interaction surfaces

- [ ] **Step 1: Normalize live diagnostics styles and page header**

Remove selectors duplicated by `App.axaml`. Apply the shared view title, secondary text, section label, metric, monospace, and button classes. Keep status text semantic and readable. Use accent color only for the real Live status and keep Connecting and Disconnected neutral.

- [ ] **Step 2: Improve live overview hierarchy**

Preserve the current metric bindings and tab structure. Use spacing and one subtle containing surface to group the managed heap and allocation rate, then use sparse separators for generation and heap-detail groups. Do not turn the metric groups into three equal cards.

- [ ] **Step 3: Refine the GC timeline toolbar and detail region**

Keep generation and pause filters, summary counts, DataGrid columns, selected-event bindings, progress bars, and all empty states. Align controls as a compact filter toolbar and place the selected collection detail in a subtle surface. Preserve every automation property.

- [ ] **Step 4: Refine comparison pickers and filters**

Keep the before and after two-column relationship, but use shared surface styling and clearer file-path hierarchy. Replace duplicated button and text styles with shared classes. Keep the arrow, choose commands, progress, cancel, error, filter bindings, summary, and DataGrid unchanged.

- [ ] **Step 5: Standardize comparison empty states**

Apply the shared empty-state treatment to the choose prompt, no-changes state, and filtered-empty state. Do not change their existing visible copy or visibility bindings.

- [ ] **Step 6: Compile both views**

Run:

```bash
rtk dotnet build MemoryProfiler.sln
```

Expected: build succeeds with all compiled bindings and XAML event handlers resolved.

- [ ] **Step 7: Commit the diagnostic surfaces**

```bash
rtk git add src/MemoryProfiler.App/Views/LiveSessionView.axaml src/MemoryProfiler.App/Views/ComparisonView.axaml
rtk git commit -m "style: unify diagnostic work surfaces"
```

---

### Task 4: Snapshot investigation and error presentation

**Files:**
- Modify: `src/MemoryProfiler.App/Views/SnapshotView.axaml`
- Modify: `src/MemoryProfiler.App/Views/ErrorDetailsView.axaml`

**Interfaces:**
- Consumes: shared visual resources from Task 1 and the user's existing retained-size progress placement
- Produces: unchanged snapshot investigation, references, roots, and error interactions

- [ ] **Step 1: Preserve the existing SnapshotView working-tree edit**

Before editing, inspect:

```bash
rtk git diff -- src/MemoryProfiler.App/Views/SnapshotView.axaml
```

Keep the retained-size progress stack in its current user-edited location above the type table grid. Do not move it back into the table-state grid.

- [ ] **Step 2: Normalize snapshot header, toolbar, and typography**

Replace duplicated local selectors with shared classes while retaining unique direction-button and investigation-specific selectors. Preserve Close, navigation, refresh, cancel, retained-size, context-menu, double-click, and copy command bindings.

- [ ] **Step 3: Refine type and instance panes**

Use 8 px containing surfaces with quieter borders, 4 px compact controls, and subtle splitters. Preserve the current grid proportions, minimum widths, filter bindings, table columns, loading skeletons, error states, empty states, and selection bindings.

- [ ] **Step 4: Refine references and path-to-root panes**

Use consistent headers, direction controls, status summaries, loading indicators, tables, and empty states. Keep `OnReferencesListDoubleTapped` and `OnPathsListDoubleTapped`, all context-menu commands, and every object/reference/root binding unchanged.

- [ ] **Step 5: Refine contextual errors**

Keep `ErrorDetailsView` bindings and automation behavior. Apply the shared 8 px surface radius, error surface brush, 13 px message hierarchy, and a quieter expandable details region. Do not alter error copy or recovery commands.

- [ ] **Step 6: Compile the snapshot view**

Run:

```bash
rtk dotnet build MemoryProfiler.sln
```

Expected: build succeeds and all snapshot compiled bindings and event handlers resolve.

- [ ] **Step 7: Commit snapshot presentation**

```bash
rtk git add src/MemoryProfiler.App/Views/SnapshotView.axaml src/MemoryProfiler.App/Views/ErrorDetailsView.axaml
rtk git commit -m "style: refine snapshot investigation UI"
```

---

### Task 5: Final Fluent 2 verification

**Files:**
- Inspect: `src/MemoryProfiler.App/App.axaml`
- Inspect: `src/MemoryProfiler.App/MainWindow.axaml`
- Inspect: `src/MemoryProfiler.App/Views/LiveSessionView.axaml`
- Inspect: `src/MemoryProfiler.App/Views/ComparisonView.axaml`
- Inspect: `src/MemoryProfiler.App/Views/SnapshotView.axaml`
- Inspect: `src/MemoryProfiler.App/Views/ErrorDetailsView.axaml`

**Interfaces:**
- Consumes: all prior task outputs
- Produces: verified Fluent 2 visual pass with no intended behavioral diff

- [ ] **Step 1: Run the solution build**

```bash
rtk dotnet build MemoryProfiler.sln
```

Expected: build succeeds with zero errors.

- [ ] **Step 2: Check XAML and whitespace integrity**

```bash
rtk git diff --check
rtk git status --short
```

Expected: no whitespace errors; only the intended UI files and any pre-existing unrelated files appear.

- [ ] **Step 3: Audit binding and interaction preservation**

```bash
rtk git diff -- src/MemoryProfiler.App/App.axaml src/MemoryProfiler.App/MainWindow.axaml src/MemoryProfiler.App/Views/LiveSessionView.axaml src/MemoryProfiler.App/Views/ComparisonView.axaml src/MemoryProfiler.App/Views/SnapshotView.axaml src/MemoryProfiler.App/Views/ErrorDetailsView.axaml
```

Confirm that changes are limited to resources, styles, layout, and presentation. Confirm every removed binding, command, event handler, automation property, and visibility condition was relocated intact or intentionally left unchanged.

- [ ] **Step 4: Run the design pre-flight audit**

Confirm one accent family, system light and dark theme support, the documented radius hierarchy, readable button contrast, no decorative motion, no new dependencies, no card-heavy metric grid, complete loading and empty states, and no em-dash or en-dash characters in visible UI copy.

- [ ] **Step 5: Record final verification state**

```bash
rtk git status --short
rtk git log -5 --oneline
```

Expected: the implementation commits are present and unrelated working-tree changes remain untouched.
