# Compact command surface

## Reference source

- shadcn/ui: https://ui.shadcn.com/docs/components/command
- Raycast: https://www.raycast.com/
- Linear: https://linear.app/

## What we like

- A small trigger with a predictable keyboard path to grouped actions.
- Search/filter reduces interaction cost without moving the user into a separate workflow.
- The active, disabled, and destructive states are obvious but visually restrained.

## What we do not want

- A full-screen modal for a handful of contextual actions.
- Web-style command palette visuals pasted into the desktop editor.
- Commands that bypass safety checks or hide why an action is disabled.

## Lasero adaptation

Prefer WPF `ContextMenu`, `MenuItem`, or an existing Lasero dialog when a command surface is needed.
Keep the trigger near the current panel, preserve accessible names and existing keyboard shortcuts,
and route all machine actions through their existing commands and preflight checks.
