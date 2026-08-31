# Navigation History Design

## Goal

Preserve investigation context while users move from heap types to instances, references, and GC-root paths, with familiar Back and Forward controls.

## Design

Add an `InvestigationNavigationService` that owns a current location plus back and forward stacks. Locations are immutable typed values for the type list, a selected type, an object's outgoing or incoming references, and an object's paths to GC roots. Navigating pushes the previous location onto the back stack and clears the forward stack; replaying history moves entries between stacks without creating new history.

`SnapshotViewModel` translates existing selection and context-menu actions into locations. It applies a location by restoring the relevant type selection or loading the requested reference/root pane through the existing cancellable view models. A replay guard prevents restored selections from adding duplicate entries.

The snapshot header gains compact Back and Forward buttons using the existing Avalonia FluentTheme resources. Buttons expose accessible names, tooltips, keyboard focus, and disabled states. No animation or new dependency is required.

## Testing

- Unit-test stack behavior, forward-history invalidation, duplicate navigation, and notifications.
- Unit-test `SnapshotViewModel` history integration and async restoration.
- Build and run the full solution test suite.
