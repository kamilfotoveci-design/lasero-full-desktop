---
name: Precision Industrial Production
colors:
  surface: '#faf8ff'
  surface-dim: '#d9d9e5'
  surface-bright: '#faf8ff'
  surface-container-lowest: '#ffffff'
  surface-container-low: '#f3f3fe'
  surface-container: '#ededf9'
  surface-container-high: '#e7e7f3'
  surface-container-highest: '#e1e2ed'
  on-surface: '#191b23'
  on-surface-variant: '#434655'
  inverse-surface: '#2e3039'
  inverse-on-surface: '#f0f0fb'
  outline: '#737686'
  outline-variant: '#c3c6d7'
  surface-tint: '#0053db'
  primary: '#004ac6'
  on-primary: '#ffffff'
  primary-container: '#2563eb'
  on-primary-container: '#eeefff'
  inverse-primary: '#b4c5ff'
  secondary: '#585f6c'
  on-secondary: '#ffffff'
  secondary-container: '#dce2f3'
  on-secondary-container: '#5e6572'
  tertiary: '#943700'
  on-tertiary: '#ffffff'
  tertiary-container: '#bc4800'
  on-tertiary-container: '#ffede6'
  error: '#ba1a1a'
  on-error: '#ffffff'
  error-container: '#ffdad6'
  on-error-container: '#93000a'
  primary-fixed: '#dbe1ff'
  primary-fixed-dim: '#b4c5ff'
  on-primary-fixed: '#00174b'
  on-primary-fixed-variant: '#003ea8'
  secondary-fixed: '#dce2f3'
  secondary-fixed-dim: '#c0c7d6'
  on-secondary-fixed: '#151c27'
  on-secondary-fixed-variant: '#404754'
  tertiary-fixed: '#ffdbcd'
  tertiary-fixed-dim: '#ffb596'
  on-tertiary-fixed: '#360f00'
  on-tertiary-fixed-variant: '#7d2d00'
  background: '#faf8ff'
  on-background: '#191b23'
  surface-variant: '#e1e2ed'
typography:
  headline-home:
    fontFamily: Inter
    fontSize: 26px
    fontWeight: '600'
    lineHeight: 32px
    letterSpacing: -0.02em
  section-header:
    fontFamily: Inter
    fontSize: 16px
    fontWeight: '600'
    lineHeight: 24px
  body-base:
    fontFamily: Inter
    fontSize: 14px
    fontWeight: '400'
    lineHeight: 20px
  body-compact:
    fontFamily: Inter
    fontSize: 13px
    fontWeight: '400'
    lineHeight: 18px
  metadata:
    fontFamily: Inter
    fontSize: 12px
    fontWeight: '500'
    lineHeight: 16px
    letterSpacing: 0.01em
  label-uppercase:
    fontFamily: Inter
    fontSize: 11px
    fontWeight: '700'
    lineHeight: 16px
    letterSpacing: 0.05em
rounded:
  sm: 0.25rem
  DEFAULT: 0.5rem
  md: 0.75rem
  lg: 1rem
  xl: 1.5rem
  full: 9999px
spacing:
  unit: 4px
  container-padding: 24px
  element-gap: 12px
  sidebar-width: 240px
  toolbar-height: 48px
  gutter: 16px
---

## Brand & Style

The design system is engineered for a professional Windows desktop environment, prioritizing utility and clarity over decorative elements. It draws heavily from **Minimalism** and **Modern Corporate** aesthetics, mirroring the high-stakes precision of creative production software like CAD or professional vector editors.

The interface evokes a sense of reliability and technical mastery. It uses a neutral, warm-white canvas to reduce eye strain during long production sessions, while using high-contrast typography and specific accent colors to guide the operator's eye to critical status indicators and action triggers. The visual language is disciplined: every pixel must serve a functional purpose in the engraving workflow.

## Colors

The palette is anchored by a warm-white application background (`#F7F7F5`), which provides a soft but distinct separation from the pure white (`#FFFFFF`) of the functional surfaces and tool containers. 

- **Primary Blue:** Reserved for the "Start" or "Primary Action" state, indicating the main path forward.
- **Grayscale:** Uses a nearly black `#171717` for text to ensure maximum legibility against white surfaces, and a medium gray `#6B7280` for labels and non-essential metadata.
- **Status Colors:** Green and Red are used sparingly and strictly for machine status (Ready, Success) or critical failures/stop commands.
- **Borders:** A subtle `#E5E7EB` is the primary method of defining hierarchy, replacing shadows to maintain a "flat" professional look.

## Typography

This design system utilizes **Inter** for its neutral, highly legible character at small scales typical of desktop applications. 

- **Hierarchy:** Contrast is achieved through weight rather than dramatic size shifts. Section headers use a semibold weight to anchor content blocks.
- **Numerical Data:** For coordinate inputs, power levels, and speed settings, ensure tabular lining is enabled to keep numbers aligned.
- **Legibility:** Body text is set at 13px or 14px to balance information density with readability. Metadata (12px) is used for secondary machine stats or file paths.

## Layout & Spacing

The layout follows a **Fixed Sidebar + Fluid Content** model optimized for wide-screen desktop displays. 

- **Grid:** A 12-column grid is used within the main content area, though many "Tool" views will rely on a split-pane layout (Canvas in the center, Properties on the right).
- **Rhythm:** An 8px/4px base unit ensures consistent alignment. 
- **Density:** High-density spacing is used for property panels (input fields, sliders) to maximize the visible toolset, while larger margins (24px) are used on landing/dashboard views to provide breathing room.
- **Reflow:** On smaller window sizes, the side panels may collapse into icon-only bars to preserve the workspace area.

## Elevation & Depth

This design system avoids the use of traditional shadows to signify depth, opting instead for **Tonal Layers** and **Low-contrast Outlines**.

- **Level 0 (Background):** `#F7F7F5` - The base of the application.
- **Level 1 (Cards/Panels):** `#FFFFFF` with a 1px solid `#E5E7EB` border.
- **Level 2 (Modals/Popovers):** `#FFFFFF` with a 1px solid `#D1D5DB` border and a very subtle, large-radius ambient shadow (4% opacity black) only to provide separation from the tool canvas.
- **Active State:** Elements being dragged or interacted with may use a thicker primary-colored border (`#2563EB`) rather than an elevation lift.

## Shapes

The design uses a consistent **8px (0.5rem)** radius for standard components like cards, inputs, and buttons. 

- **Small Components:** Tags, chips, and checkboxes use `rounded-sm` (4px).
- **Large Components:** Large project thumbnails or onboarding containers use `rounded-lg` (16px).
- **Icons:** Use a 1.5px or 2px stroke width with slightly rounded caps to match the UI's geometry.

## Components

### Buttons
- **Primary:** Solid `#2563EB` with white text. High contrast, reserved for "Start" or "Save".
- **Secondary:** White background with `#E5E7EB` border and `#171717` text.
- **Action (Ghost):** No background or border until hover. Used for toolbar icons and minor settings.

### Input Fields
- **Text/Numeric Inputs:** Height of 32px for compact density. 1px border. Focus state uses a 2px blue ring.
- **Unit Labels:** Affixed labels (e.g., "mm", "mm/s") are placed inside the right edge of the input in the `metadata` type style.

### Project Cards
- **Visuals:** Top 70% of the card is a project thumbnail with a light gray background. 
- **Content:** Bottom 30% contains the title and a metadata row showing "Last modified" or "Material type".

### Status Badges
- Small, pill-shaped indicators.
- **Ready:** Green text on light green tint.
- **Busy/Engraving:** Blue text on light blue tint.
- **Disconnected:** Gray text on light gray tint.

### Lists
- Sidebar navigation items should have an 8px left-accent bar or background tint (`#F3F4F6`) when active.
- Icons in lists are 20x20px, stroke-based.