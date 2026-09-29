# Lasero UI/UX Research Workflow

Use this workflow before any meaningful UI/UX change in Lasero.

## 1. Inspect current behavior
Document the current component/screen, user flow, hierarchy, design tokens, icons, spacing, radii, motion, keyboard behavior, loading/error/empty states, and functionality that must not regress.

## 2. Define the UX problem
Describe the problem in one sentence. Solve a user problem, not “make it prettier”.

## 3. Research approved references
When web access is available, inspect relevant patterns from:
1. 21st.dev
2. shadcn/ui
3. React Bits
4. Motion Primitives
5. Magic UI
6. Aceternity UI

Also inspect Linear, Figma, Claude, ChatGPT, Notion, Raycast, Arc, Framer, and LightBurn when relevant.

For substantial work, compare at least 3 references.

Evaluate:
- usability
- information hierarchy
- interaction cost
- visual noise
- accessibility
- keyboard support
- desktop suitability
- implementation complexity
- performance
- consistency with Lasero

## 4. Compare references
Use:

| Reference | Useful idea | Problem / limitation | Fit for Lasero |
|---|---|---|---|
| A | ... | ... | High |
| B | ... | ... | Medium |
| C | ... | ... | Low |

Extract principles; do not copy an entire component blindly.

## 5. Translate into Lasero
Adapt the interaction to Lasero's existing colors, typography, spacing, radii, icons, borders, shadows, motion, controls, and desktop interaction model.

## 6. Lasero quality bar
The result should feel precise, calm, professional, modern, desktop-native, fast, minimal, and coherent.

Avoid:
- AI slop
- excessive gradients
- giant glowing surfaces
- glassmorphism everywhere
- huge rounded cards
- card-inside-card layouts
- giant empty states
- unnecessary motion
- marketing-site aesthetics

## 7. Specify states before coding
Consider:
- default
- hover
- focus
- active
- selected
- disabled
- loading
- success
- error
- empty

For complex components also define keyboard behavior, cancellation, responsiveness, scrolling, animation, and undo/redo where relevant.

## 8. Implement the smallest coherent change
Prefer improving existing architecture and hierarchy over replacing whole screens. Do not change unrelated backend behavior.

## 9. Motion rules
Use motion only for functional feedback.

Suggested timing:
- hover/focus: 120-180 ms
- enter/exit: 160-220 ms
- collapse/expand: 200-280 ms

Prefer ease-out and respect reduced motion.

## 10. Review
Ask:
- Is it easier to understand?
- Is it faster?
- Does it require fewer clicks?
- Is it less noisy?
- Is keyboard behavior preserved or improved?
- Does it feel like Lasero?
- Did anything regress?

## 11. Report
At completion report:
- problem solved
- references inspected
- principles borrowed
- files changed
- behavior changes
- visual changes
- accessibility notes
- build/test results
- remaining UX issues

## 12. Licensing / dependencies
Never copy source code from a reference website unless its license explicitly permits it.

Use references primarily to learn interaction patterns. Prefer reimplementing them with Lasero's existing architecture and design system. Do not add a dependency for one small visual effect.
