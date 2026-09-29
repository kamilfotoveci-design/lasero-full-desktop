# Contextual empty state

## Reference source

- Claude: https://support.claude.com/en/articles/9487310-what-are-artifacts
- 21st.dev: https://21st.dev/
- Linear: https://linear.app/

## What we like

- A short explanation of what the current workspace action can do.
- A small number of contextual starters that reduce first-action uncertainty.
- Visual hierarchy comes from spacing and type, not a large illustration or card stack.

## What we do not want

- A giant AI headline, robot artwork, or generic marketing copy.
- Suggestions that ignore the selected object, material, machine, or current task.
- More than one competing primary action.

## Lasero adaptation

Keep the empty state compact and centered in the available panel width. Reuse Lasero avatar/icon and
existing suggestion-button styles. Generate suggestions from workspace context when available; fall
back to a small stable set when it is not.
