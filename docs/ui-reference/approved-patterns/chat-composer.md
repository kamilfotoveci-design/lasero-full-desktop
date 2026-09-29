# Chat composer

## Reference source

- Claude: https://support.claude.com/en/articles/9487310-what-are-artifacts
- shadcn/ui: https://ui.shadcn.com/docs/components/textarea

## What we like

- One compact multiline input with an obvious send state.
- Enter sends; Shift+Enter creates a newline.
- Attachments and contextual actions stay close to the composer instead of becoming a second toolbar.
- Focus, disabled, error, and busy states are explicit and local to the input.

## What we do not want

- A giant hero composer, decorative glow, or marketing-style prompt box.
- A web-only component dependency for a small WPF interaction.
- Clearing or losing a draft when a request fails.

## Lasero adaptation

Use the existing WPF `TextBox`, `Button`, `Button.Ghost`/`Button.Primary`, `Brush.*`, `Size.Control.*`,
and keyboard conventions. Auto-grow within a bounded height; keep attachments as compact chips with a
real remove action once the chat data contract supports them. Preserve the draft on failures.
