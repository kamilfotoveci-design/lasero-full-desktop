# Lasero UI Component Reference Sources

Use these sources for UI/UX research and interaction inspiration.

## Primary

### 21st.dev
https://21st.dev/
Use for AI chat, prompt composers, agent interfaces, command palettes, empty states, sidebars, and modern product patterns.

### shadcn/ui
https://ui.shadcn.com/
Use for dialogs, dropdowns, popovers, tooltips, command menus, tabs, forms, scroll areas, and restrained UI primitives.

### React Bits
https://reactbits.dev/
Use for microinteractions, subtle transitions, loading states, text animation, and hover feedback.

### Motion Primitives
https://motion-primitives.com/
Use for restrained enter/exit, progressive disclosure, and layout transitions.

## Secondary

### Magic UI
https://magicui.design/
Use sparingly for loaders, shimmer, animated borders, empty states, and polish.

### Aceternity UI
https://ui.aceternity.com/
Use mainly for inspiration around experimental interactions, onboarding, and AI states. Avoid excessive glow, gradients, particles, and glassmorphism.

## Product references

### Linear
Density, hierarchy, command palette, sidebars, restrained motion.

### Figma
Canvas interactions, property inspectors, selection, tool modes, overlays, context menus.

### Claude
AI composer, attachments, clean response layout, tool/progress presentation.

### ChatGPT
Streaming, tool/result cards, message actions, multimodal input, scrolling.

### Raycast
Compact desktop UI, command workflows, keyboard navigation.

### Notion
Inline controls, contextual actions, content hierarchy.

### LightBurn
Laser-specific workflows, vector editing, layers/output semantics, booleans, offsets, transforms.

For vector work also read:
`docs/reference/LIGHTBURN_VECTOR_PARITY.md`

## Selection guide

### AI chat / KAMIL
Prioritize Claude, 21st.dev, ChatGPT, shadcn/ui, then React Bits / Motion Primitives.

### Vector editor
Prioritize LightBurn, Figma, and professional CAD/vector conventions.

### Settings / forms
Prioritize Linear, Figma, shadcn/ui.

### Command palette
Prioritize Raycast, Linear, shadcn/ui.

### Empty states
Prioritize Linear, Claude, 21st.dev.

## Evaluation checklist
Ask:
- Does it solve the actual UX problem?
- Is it suitable for desktop software?
- Does it reduce interaction cost?
- Is it keyboard friendly?
- Is it quiet enough visually?
- Can it use existing Lasero controls?
- Does it fit Lasero tokens?
- Will it remain performant?
- Does it require a new dependency?
- Is that dependency worth it?

Never choose a pattern only because it looks impressive.

## Licensing
Never copy source code unless its license explicitly allows it. Prefer reimplementing the interaction with Lasero's existing architecture and design system.
