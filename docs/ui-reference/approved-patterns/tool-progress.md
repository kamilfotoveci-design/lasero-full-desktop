# User-visible tool progress

## Reference source

- ChatGPT: https://help.openai.com/en/articles/9309188-chatgpt-can-now-see-hear-and-speak
- shadcn/ui: https://ui.shadcn.com/docs/components/progress
- Motion Primitives: https://motion-primitives.com/

## What we like

- Progress is local to the action that started it.
- Completed and pending user-visible actions can be disclosed compactly.
- Expand/collapse is available without forcing a large status card into the workflow.

## What we do not want

- Private chain-of-thought, internal reasoning, or fabricated tool steps.
- A looping typewriter effect, shimmer, glow, or progress animation without real state.
- Forced scrolling while the user is reading older content.

## Lasero adaptation

Expose only backend-provided, user-safe action labels. Use a compact WPF `Expander` near the chat
composer, existing status colors, and restrained transitions. When the API has only a busy boolean,
show a truthful generic processing state rather than inventing a checklist. Keep scroll position when
the user is above the bottom and reveal a scroll-to-bottom affordance.
