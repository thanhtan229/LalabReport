# Tinix UX/UI Standard

> **Shared UX/UI contract for Tinix-family products**
>
> This document defines the default visual language, interaction model, design tokens, component behavior, accessibility baseline, system-state behavior, and UI quality rules for this repository.
>
> It is intentionally **framework-agnostic, architecture-agnostic, and product-type-aware**.

---

## 0. Document Status

**Standard:** Tinix UX/UI Standard  
**Version:** 1.0  
**Status:** Active  
**Scope:** Frontend, desktop UI, web UI, mobile UI, embedded UI, and user-facing workflow surfaces  
**Default mode:** Light theme unless the project profile explicitly requires otherwise

### 0.1. Rule Strength

The following keywords are normative:

- **MUST / MUST NOT** — mandatory unless explicitly overridden by a documented project requirement.
- **SHOULD / SHOULD NOT** — default behavior; deviate only when there is a concrete UX reason.
- **MAY** — optional and dependent on product needs.
- **PREFERRED** — recommended when multiple valid solutions exist.

When a rule conflicts with a stronger project requirement, follow the source-of-truth order defined later in this document.

---

# 1. Purpose

Tinix applications do not need to share identical layouts.

They **MUST feel like members of the same product family**.

This standard exists to keep consistency in:

- visual identity;
- typography;
- color semantics;
- spacing;
- component behavior;
- navigation;
- feedback;
- form behavior;
- motion;
- accessibility;
- content hierarchy;
- UX writing;
- responsiveness;
- data density;
- loading and error behavior;
- long-running operations;
- design implementation quality.

The goal is not visual sameness.

The goal is **consistent product character, interaction logic, and implementation discipline**.

---

# 2. Core Product Philosophy

## 2.1. Clarity Before Decoration

The interface MUST make the next meaningful action easy to identify.

Prefer:

- clear hierarchy;
- descriptive labels;
- predictable controls;
- meaningful whitespace;
- visible system status;
- strong grouping;
- restrained visual emphasis.

Avoid:

- decorative complexity;
- excessive gradients;
- excessive shadows;
- unnecessary animation;
- ambiguous icon-only actions;
- visual effects that compete with content;
- ornamental UI that does not improve comprehension.

---

## 2.2. Professional but Warm

Tinix products SHOULD feel:

- polished;
- professional;
- approachable;
- editorial;
- modern;
- calm;
- trustworthy;
- precise;
- crafted.

Avoid interfaces that feel:

- aggressively futuristic;
- game-like;
- visually noisy;
- sterile without purpose;
- overly technical when the user does not need technical detail.

---

## 2.3. Predictable Interaction

The same interaction MUST behave consistently across the application.

Examples:

- primary actions use the same visual hierarchy;
- destructive actions always look destructive;
- validation errors appear in consistent locations;
- loading indicators follow consistent patterns;
- dialogs follow consistent behavior;
- navigation active states are recognizable;
- similar keyboard interactions behave similarly.

A local screen MUST NOT silently invent a second interaction model.

---

## 2.4. Progressive Disclosure

Show the user what is needed first.

Advanced or infrequently used controls SHOULD be:

- collapsed;
- placed under advanced settings;
- moved to secondary actions;
- shown contextually;
- revealed only when relevant.

Do not expose complexity merely because the system supports it.

---

## 2.5. Human-Readable System State

The UI MUST communicate, when relevant:

- what is happening;
- what completed;
- what failed;
- what is still pending;
- whether progress was preserved;
- whether user action is required;
- what the user can do next.

The UI MUST NOT appear frozen during meaningful processing.

---

## 2.6. Restraint Over Novelty

When choosing between a familiar pattern and a visually novel pattern, prefer the familiar pattern unless the novel pattern materially improves the workflow.

Tinix is a productivity-oriented design language.

Novelty is not a goal by itself.

---

# 3. Project UI Profile

Every repository using this standard SHOULD complete this section.

Do not invent project-specific values if they are not known. Leave them as `TBD` until they are decided.

```text
Product name: Lalab Auto Report

Product type:
- Productivity
- Data / Admin

Primary platform: Windows Desktop (WPF, .NET 10)
Secondary platform: None (Desktop only)

Density:
- Comfortable (Default UI)
- Compact (Data tables & Order items)

Primary navigation: Left sidebar (Productivity shell)
Secondary navigation: Scoped tabs / Filter pills

Primary input:
- Mouse + keyboard

Responsive behavior:
- Desktop-only (Min width: 950px, Min height: 600px; Optimal: 1240x780px)

Dark mode:
- Not required (V1 Light mode default with warm surfaces)

Main workspace model: Sidebar + Fluid main workspace + Filter toolbar + Master-detail order lists

Special UI needs:
- Data tables
- Long-running jobs (Filesystem scanning with live progress)
- File browser integration (Explorer open actions)
- Batch processing & Scoped rescanning
- Status pill badges (Needs Review, Ready, Locked)
```

Project-specific UI decisions in this section SHOULD remain short and structural.

Detailed one-off screen behavior belongs in feature specifications or project documentation.

---

# 4. Source of Truth and Overrides

Use the following priority unless the project explicitly defines another order:

```text
1. Latest explicit user or product decision
2. Project-specific overrides in this UX_UI.md
3. PLAN_lalab.md locked domain & business rules
4. This shared UX/UI standard
5. Existing shared design tokens and components
6. Individual screen implementation
```

If an individual screen conflicts with the design system, the screen SHOULD normally be corrected rather than redefining the system.

## 4.1. Project Overrides

Document significant deviations here.

| Rule / Area | Override | Reason |
|---|---|---|
| Order Grouping | Physical folder = Order. Never merge multiple customer folders into one physical order | Locked domain rule from PLAN_lalab.md (provenance preservation). |
| Billing Folder & Ambiguity | Effective Billing Folder resolves automatically for single linear leaves; multiple competing leaf folders require explicit human choice in Ambiguous Leaf Folders modal | Locked billing & ambiguity rule from PLAN_lalab_V2.md (correctness over automation). |
| Print Folder Resolution | Never silently pick among multiple leaf candidates; require manual selection | Locked ambiguity rule from PLAN_lalab.md & V2. |
| Typography Font Fallback | Display font uses "Georgia", UI font uses "Segoe UI" | Native Windows system fonts guarantee 100% crisp Vietnamese UTF-8 rendering without external asset overhead. |
| Sidebar Theme | Warm Light Neutral Sidebar (`#EAE8E4` / `#F1EFEA`) with subtle border `#CFCCCB` | Aligns with Tinix Light mode default and serene photo-lab workflow. |

Rules:

- major deviations MUST be documented;
- the reason MUST be concrete;
- unaffected Tinix principles MUST remain intact;
- local exceptions MUST NOT silently become a second design system.

---

# 5. Visual Identity

Tinix visual identity SHOULD communicate:

- reliability;
- precision;
- warmth;
- creative confidence;
- productivity;
- modern craftsmanship.

The interface SHOULD remain visually restrained enough for long working sessions.

## 5.1. Brand Accent Discipline

Tinix orange is a deliberate accent.

Use it primarily for:

- primary calls to action;
- active/focus emphasis;
- important selected states;
- limited brand moments.

Do not use it for:

- every heading;
- every icon;
- every badge;
- every border;
- large decorative areas without purpose.

The interface MUST NOT feel flooded with brand color.

---

# 6. Design Token Architecture

Projects SHOULD organize design values in three layers:

```text
Primitive tokens
↓
Semantic tokens
↓
Component tokens
```

Example:

```css
/* Primitive */
--orange-500: #FF4800;
--orange-700: #C93700;

/* Semantic */
--color-action-primary: var(--orange-500);
--color-action-primary-hover: var(--orange-700);

/* Component */
--button-primary-bg: var(--color-action-primary);
```

Semantic roles are the important cross-project contract.

Technology-specific implementation MAY differ.

## 6.1. Token Rule

Colors, spacing, radius, typography, elevation, and motion SHOULD come from shared tokens rather than arbitrary component-local values.

One-off hard-coded values require a clear reason.

---

# 7. Typography

Tinix uses a two-family typography system.

## 7.1. Display Font

```css
--font-display: "Lora", Georgia, serif;
```

Preferred use:

- main page titles;
- hero titles;
- selected editorial emphasis.

Default page title:

```css
font-family: var(--font-display);
font-size: 32px;
font-weight: 400;
line-height: 1.25;
letter-spacing: -0.02em;
```

Do not overuse the display font in dense operational interfaces.

---

## 7.2. UI Font

```css
--font-ui: "Plus Jakarta Sans", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
```

Use for:

- body copy;
- navigation;
- buttons;
- forms;
- tables;
- labels;
- dialogs;
- secondary headings;
- toolbars;
- inspectors.

Default body:

```css
font-family: var(--font-ui);
font-size: 14px;
line-height: 1.5;
```

---

## 7.3. Typography Hierarchy

```text
Display / Page Title     32px / 400
Section Heading          20–24px / 600
Subsection Heading       16–18px / 600
Body                     14px / 400
Small / Secondary        12–13px / 400–500
Label                    12–13px / 600
Eyebrow                  11px / 700 / uppercase
Caption                   11–12px / 400
```

Projects MAY adapt scale slightly for platform constraints while preserving hierarchy.

---

## 7.4. Eyebrow Labels

```css
font-size: 11px;
font-weight: 700;
text-transform: uppercase;
letter-spacing: 0.14em;
color: var(--color-primary);
```

Use sparingly.

---

## 7.5. Font Fallback Policy

If the preferred font is unavailable on a native platform:

- use the closest approved fallback;
- preserve serif-vs-sans role;
- do not introduce a third visual font family without a documented reason.

---

# 8. Color System

All UI colors SHOULD be expressed through semantic design tokens.

## 8.1. Brand Colors

```css
:root {
  --color-primary: #FF4800;
  --color-primary-hover: #C93700;
  --color-primary-active: #9F2800;
  --color-primary-subtle: rgba(255, 72, 0, 0.10);
  --color-primary-focus: rgba(255, 72, 0, 0.15);

  --color-teal-dark: #124548;
  --color-teal-medium: #2F7579;

  --color-navy-deep: #15295A;
  --color-navy-medium: #180BB1;
}
```

---

## 8.2. Neutral Surfaces

```css
:root {
  --color-bg-body: #F8F5EE;
  --color-bg-surface: #FFFFFF;
  --color-bg-subtle: #FCFCFA;

  --color-border: #CFCCCB;
  --color-border-subtle: #EAE8E4;

  --color-text-primary: #1F1F1F;
  --color-text-secondary: #5F6368;
  --color-text-muted: #9B9897;
}
```

Projects MAY adapt the surface palette for environmental needs while preserving semantic hierarchy.

---

## 8.3. Semantic Status Colors

```css
:root {
  --color-success: #124548;
  --color-success-bg: #DFF2E7;
  --color-success-border: #B8E4CC;

  --color-warning: #8A5700;
  --color-warning-bg: #FFF0C9;
  --color-warning-border: #E9C66E;

  --color-danger: #D9002B;
  --color-danger-bg: #F7DFDC;
  --color-danger-border: #E8A4A1;

  --color-info: #15295A;
  --color-info-bg: #CAEBFF;
  --color-info-border: #A6DCFF;
}
```

Status MUST NOT be communicated by color alone.

Pair color with one or more of:

- text;
- icon;
- label;
- badge;
- accessible name.

---

## 8.4. Semantic Color Discipline

Preferred semantic roles:

```text
Orange   → primary action / Tinix emphasis
Green    → success
Amber    → warning
Red      → destructive / error
Blue     → informational
Gray     → neutral / inactive / secondary
```

Do not use destructive red as a decorative accent.

---

# 9. Spacing, Radius, and Elevation

## 9.1. Spacing

Tinix uses an 8px-oriented spacing scale with 4px half-step support.

```css
--space-2xs: 4px;
--space-xs: 8px;
--space-sm: 12px;
--space-md: 16px;
--space-lg: 20px;
--space-xl: 24px;
--space-2xl: 32px;
--space-3xl: 40px;
--space-4xl: 64px;
```

Use tokens instead of arbitrary spacing where practical.

Structured whitespace is preferred over excessive separators.

---

## 9.2. Border Radius

```css
--radius-sm: 4px;
--radius-md: 8px;
--radius-lg: 16px;
--radius-xl: 24px;
--radius-full: 9999px;
```

Recommended usage:

```text
4px      compact chips / small controls
8px      buttons / inputs / selects
16px     cards / panels / dialogs
24px     hero / feature containers
full     badges / avatars / pills
```

---

## 9.3. Elevation

Tinix favors relatively flat layouts with selective depth.

```css
--shadow-base: none;
--shadow-raised:
  0 2px 4px rgba(33, 51, 67, 0.08),
  0 1px 2px rgba(33, 51, 67, 0.04);

--shadow-elevated:
  0 4px 12px rgba(33, 51, 67, 0.10),
  0 2px 4px rgba(33, 51, 67, 0.06);

--shadow-floating:
  0 20px 40px rgba(0, 0, 0, 0.16);
```

Recommended:

- `raised` → cards and bounded surfaces;
- `elevated` → selected/hovered or strongly separated surfaces;
- `floating` → modal, popover, floating overlay.

Do not create depth by stacking multiple heavy shadows.

---

# 10. Layout and App Shell Profiles

There is no single mandatory Tinix layout.

Choose the shell that matches the product type.

## 10.1. Utility

Best for small single-purpose tools.

```text
Header
+
Primary workspace
+
Optional settings / status region
```

Do not force a sidebar when navigation depth does not justify one.

---

## 10.2. Productivity

Preferred default for workflow-heavy desktop apps.

```text
Fixed or collapsible sidebar
+
Fluid main workspace
+
Optional detail panel
```

Typical sidebar width:

```text
220–260px
```

Typical workspace max width:

```text
1280–1440px
```

Full-width workspaces MAY exceed this when the workflow requires it.

---

## 10.3. Data / Admin

Typical structure:

```text
Sidebar
+
Page toolbar
+
Search / filters
+
Data grid / list
+
Optional detail drawer
```

High density is acceptable when readability remains strong.

---

## 10.4. Creative Workspace

Typical structure:

```text
Top bar
+
Left tool rail
+
Central canvas / preview / workspace
+
Optional right inspector
+
Optional timeline / lower panel
```

Canvas space SHOULD have priority over decorative chrome.

Panels MAY be resizable when that materially improves the workflow.

---

## 10.5. SaaS / Responsive Web

Typical behavior:

```text
Desktop      → sidebar or top navigation
Tablet       → compact sidebar / drawer
Mobile       → drawer or bottom navigation
```

Do not simply compress desktop layout until it becomes unusable.

---

## 10.6. Mobile

Use mobile-native patterns such as:

- stack navigation;
- bottom navigation;
- tab navigation;
- contextual action sheets;
- sheets;
- touch-first controls.

Do not reproduce desktop sidebars directly on mobile.

---

# 11. Density and Responsive Behavior

## 11.1. Density Profiles

### Compact

Use for:

- data-heavy interfaces;
- admin screens;
- editors;
- professional desktop tools.

Typical control height:

```text
32–36px
```

Dense spacing is allowed.

---

### Comfortable — Default

Use for most desktop and web products.

Typical control height:

```text
36–40px
```

This is the default Tinix density unless the project profile says otherwise.

---

### Touch

Use when touch is a primary input.

Interactive target SHOULD generally be at least:

```text
44px
```

Use larger spacing and stronger separation.

---

## 11.2. Responsive Philosophy

Breakpoints SHOULD respond to layout failure, not merely device names.

Define responsive behavior based on:

- navigation fit;
- readable content width;
- toolbar wrapping;
- panel usability;
- table usability;
- touch requirements.

Desktop-only applications SHOULD NOT overinvest in mobile behavior that is not required.

---

## 11.3. Desktop Window Resize

Desktop applications SHOULD define:

- minimum usable width;
- minimum usable height;
- sidebar collapse behavior;
- panel resizing behavior;
- overflow handling;
- maximized behavior;
- restored-window behavior.

The UI MUST NOT become structurally broken merely because the user resizes the window.

---

# 12. Navigation and Information Architecture

Navigation SHOULD be:

- predictable;
- shallow where possible;
- consistently positioned;
- visually obvious when active.

Rules:

- use one dominant primary navigation pattern per app;
- active state MUST be clearly visible;
- destructive actions SHOULD be separated from routine navigation;
- breadcrumbs SHOULD be used only when hierarchy is deep enough to justify them;
- numbered navigation MAY be used in workflow-heavy tools.

Do not create multiple competing primary navigation systems.

---

# 13. Interaction Hierarchy

Use the following action hierarchy consistently:

```text
Primary
Secondary
Tertiary / Ghost
Contextual
Destructive
```

A bounded region, form, dialog, or workflow step SHOULD normally have only one visually dominant primary action.

Do not give every action the same visual weight.

---

# 14. Component State Contract

Every interactive component MUST consider the states that are relevant to it.

Typical state set:

```text
Default
Hover
Focus-visible
Active / Pressed
Selected
Disabled
Loading
Error
Success
```

Not every component needs every state.

However, new components MUST NOT be implemented as if only the default state exists.

---

# 15. Buttons

Every project SHOULD define consistent roles:

```text
Primary
Secondary
Ghost / Tertiary
Danger
```

Rules:

- Primary = most important action in context.
- Danger = destructive or high-impact action.
- Disabled controls MUST remain legible and reject interaction.
- Loading buttons SHOULD prevent accidental double-submit.
- Icon-only buttons MUST have an accessible name.
- Icon-only buttons SHOULD usually have a tooltip when the meaning is not obvious.

---

# 16. Form Controls and Form UX

Typical comfortable desktop/web control height:

```text
36–40px
```

Compact profile MAY use:

```text
32–36px
```

Touch profile SHOULD use larger targets.

Controls SHOULD use:

- semantic border tokens;
- consistent internal padding;
- `--radius-md`;
- visible focus state.

Example focus:

```css
box-shadow: 0 0 0 3px var(--color-primary-focus);
```

Forms MUST use, where applicable:

- visible labels;
- concise help text;
- inline validation;
- errors near the relevant field;
- clear required-field indication;
- reasonable defaults;
- double-submit prevention;
- visible loading state.

Placeholder text MUST NOT be the only field label.

---

# 17. Cards and Panels

Cards SHOULD group related information.

Cards MUST NOT be used merely because a group of content exists.

Default panel language:

```text
white surface
subtle border
16px radius
restrained shadow
20–24px internal padding
```

## 17.1. No Card Soup

Do not turn every content group into a card.

Prefer:

- spacing;
- headings;
- alignment;
- subtle dividers;
- section grouping.

Use cards when the boundary has semantic meaning.

Avoid excessive nested cards.

---

# 18. Tables and Data-Dense Interfaces

Use tables when users need column-based comparison or structured scanning.

Support when relevant:

- sorting;
- filtering;
- search;
- pagination;
- virtualization;
- sticky headers;
- bulk selection;
- bulk actions;
- column resize;
- column visibility.

Do not render extremely large datasets into the DOM at once.

## 18.1. Table Content Rules

Prefer:

- text aligned consistently;
- numeric values right-aligned where useful;
- dates formatted consistently;
- predictable truncation;
- clear hover and selection states;
- explicit empty/loading/error states.

Do not use a table when a list or card layout better matches the information model.

---

# 19. Status Badges

Use compact pill-shaped badges with semantic text and color.

```text
Success     green / mint
Warning     amber / yellow
Error       red / rose
Info        blue / navy
Neutral     gray
```

Badges SHOULD be concise.

Do not use a badge for ordinary body text.

---

# 20. Overlay System

Use overlays according to purpose.

```text
Tooltip        → supplemental explanation
Dropdown       → compact choice list
Popover        → contextual controls or details
Drawer         → larger contextual workflow
Modal          → blocking decision or focused task
Toast          → transient feedback
Command palette→ power-user command/search interface
```

Rules:

- do not use a modal when inline UI, popover, or drawer is sufficient;
- do not use tooltips for essential information;
- modal dialogs MUST have a clear exit path unless the flow truly cannot continue without a decision;
- focus behavior SHOULD be correct for keyboard use.

---

# 21. System States and Feedback

Every meaningful screen or component SHOULD consider relevant system states.

## 21.1. Loading

Use the least disruptive pattern.

```text
Small control action
→ inline spinner

Local component loading
→ skeleton / component placeholder

Full-page initial load
→ structured page skeleton

Long-running process
→ progress UI with status text
```

Avoid full-screen blocking spinners for small local actions.

---

## 21.2. Empty State

A useful empty state SHOULD explain:

1. what is empty;
2. why it may be empty;
3. what the user can do next.

Avoid generic `No data` when a useful next action exists.

---

## 21.3. Error State

Errors SHOULD explain, when possible:

- what failed;
- why;
- whether progress was preserved;
- what can be retried;
- what the user should do next.

Technical details MAY be available in logs or expandable diagnostics.

---

## 21.4. Success State

Use success feedback proportionally.

```text
Small completed action
→ toast or inline confirmation

Major workflow completion
→ persistent summary

Background operation
→ status update + notification when relevant
```

Do not use a modal for every successful action.

---

## 21.5. Partial Success

When part of a batch succeeds and part fails, say so explicitly.

Prefer:

```text
8 completed, 2 failed
```

over:

```text
Failed
```

If recovery is possible, provide a targeted retry action.

---

## 21.6. Offline / Unavailable / Permission Blocked

When relevant, distinguish:

- offline;
- service unavailable;
- permission denied;
- authentication required;
- dependency unavailable.

Do not collapse all cases into a generic error.

---

# 22. Notification Hierarchy

Use:

- **Toast** → short-lived success or informational feedback;
- **Inline message** → local contextual issue;
- **Banner** → persistent page or application issue;
- **Modal** → blocking decision or irreversible confirmation.

Do not use modal dialogs as generic notifications.

---

# 23. Long-Running Operations

Long-running jobs SHOULD expose a clear lifecycle where applicable:

```text
Queued
Starting
Running
Paused
Completed
Partially completed
Failed
Cancelled
```

Show when useful:

- current state;
- progress;
- current item;
- processed count;
- success count;
- failure count;
- remaining count;
- pause/cancel controls;
- retry failed;
- completion summary.

The UI MUST NOT appear frozen.

---

# 24. Saved, Saving, and Unsaved State

For workflows with persistent user changes, consider:

```text
Saved
Saving…
Unsaved changes
Save failed
Offline
Retrying
```

Do not allow important data loss without warning or recovery when it can reasonably be prevented.

If autosave is used, expose saving failures clearly.

---

# 25. Optimistic vs Confirmed UI

Optimistic updates MAY be used for fast, reversible, low-risk actions.

Important or destructive operations SHOULD wait for backend/system confirmation before presenting final success.

Examples:

```text
Toggle local preference
→ optimistic update may be appropriate

Delete persistent data
→ confirm server/system success

Export completed
→ confirm actual output completion
```

The UI MUST NOT falsely report success.

---

# 26. Destructive Actions, Confirmation, and Undo

Destructive actions SHOULD:

- use danger styling;
- describe what will happen;
- be separated from routine actions;
- require confirmation when irreversible or high impact.

Prefer specific confirmation copy.

Good:

```text
Delete 17 selected files?
```

Avoid:

```text
Are you sure?
```

## 26.1. Reversibility Rule

If an action is easily reversible, prefer:

```text
Action
→ immediate result
→ Undo
```

over unnecessary confirmation.

Example:

```text
Archive item
→ Archived
→ Undo
```

Use confirmation when undo is not sufficient.

---

# 27. Permission UX

When requesting permissions such as:

- filesystem;
- camera;
- microphone;
- notifications;
- network access;
- third-party account access;

the UI SHOULD explain:

- what permission is needed;
- why it is needed;
- what happens if denied;
- how the user can recover later.

Avoid asking for permissions earlier than necessary.

---

# 28. Keyboard, Mouse, Touch, and Drag & Drop

## 28.1. Keyboard

Where applicable, support predictable keyboard behavior.

Common examples:

```text
Tab          → move focus
Enter        → activate / submit
Escape       → close / cancel
Arrow keys   → navigate lists or menus
Delete       → delete when contextually safe
Ctrl/Cmd+S   → save
Ctrl/Cmd+F   → find/search
Ctrl/Cmd+Z   → undo
```

Shortcuts MUST NOT be the only way to perform essential actions.

---

## 28.2. Mouse

Hover states SHOULD support discovery but MUST NOT contain essential behavior unavailable elsewhere.

Click targets SHOULD remain large enough for comfortable use.

---

## 28.3. Touch

Touch-first products SHOULD use:

- larger targets;
- stronger spacing;
- no hover dependency;
- gesture alternatives where needed.

---

## 28.4. Drag & Drop

If drag and drop is supported, provide:

- clear draggable affordance when needed;
- drop-target highlight;
- valid/invalid drop states;
- dragging state;
- recovery or undo when destructive.

Drag and drop SHOULD NOT be the only way to complete an essential action unless the product is explicitly built around that interaction.

---

# 29. Motion

Motion SHOULD support comprehension, not decoration.

Recommended:

```text
Hover / button transitions     ~150ms
Panel / tab transitions        ~200–250ms
Dialog / drawer transitions    ~200–300ms
```

Avoid:

- long animation;
- bouncing effects in productivity flows;
- continuous decorative motion;
- transitions that delay action completion.

Respect reduced-motion preferences where relevant.

---

# 30. Iconography

Use one primary icon family per project.

Rules:

- maintain consistent stroke/fill style;
- maintain consistent sizing;
- icons SHOULD reinforce text rather than replace essential labels unnecessarily;
- destructive icons MUST be unambiguous;
- icon-only actions MUST have accessible names.

Typical sizes:

```text
16px      compact
18–20px   default
24px      prominent
```

Icons SHOULD be optically aligned with adjacent text.

---

# 31. UX Writing

Tinix UI copy SHOULD be:

- concise;
- direct;
- calm;
- specific;
- human-readable.

Prefer clear verbs:

```text
Create project
Export files
Retry upload
Reconnect account
Save changes
```

Avoid vague labels such as:

```text
OK
Process
Execute
Do it
```

when a specific verb exists.

## 31.1. Error Writing

Error copy SHOULD describe the user-facing problem first.

Technical implementation details SHOULD be secondary or expandable.

---

# 32. Content Hierarchy

Recommended order:

```text
Context / eyebrow
Page title
Short explanation
Primary action
Primary content
Secondary information
Advanced controls
```

Not every page requires every layer.

The hierarchy MUST match the task rather than mechanically reproducing the template.

---

# 33. Content Resilience

UI MUST tolerate realistic content variation.

Consider:

- long names;
- long filenames;
- long paths;
- large numbers;
- missing values;
- multiline content;
- translated text;
- unexpected API text;
- narrow windows.

Use contextually appropriate:

- wrapping;
- truncation;
- ellipsis;
- tooltip;
- expandable content;
- horizontal scroll.

Content MUST NOT silently break layout.

---

# 34. Accessibility

Target **WCAG 2.2 AA where applicable**.

Minimum expectations:

- keyboard-accessible controls;
- visible focus states;
- semantic HTML or native semantics;
- labels for form controls;
- meaningful accessible names for icons;
- sufficient contrast;
- state not represented by color alone;
- appropriate heading hierarchy;
- reduced-motion consideration;
- logical focus order;
- touch-target consideration where touch is used.

Accessibility requirements MAY be stricter for specific projects.

---

# 35. Dark Mode

Dark mode is optional unless the project requires it.

If implemented:

- preserve semantic roles;
- preserve brand identity;
- do not simply invert colors;
- validate contrast;
- keep surface hierarchy distinguishable;
- use semantic tokens rather than duplicating component-specific color values.

---

# 36. Performance and Large Data UX

The UI SHOULD remain responsive under realistic workload.

For large datasets:

- paginate or virtualize when appropriate;
- avoid rendering unbounded lists;
- avoid blocking the main UI during expensive operations;
- expose progress when work is meaningful;
- preserve user context during refreshes where possible.

Performance issues that make the interface appear frozen are UX issues.

---

# 37. Component Reuse and Design-System Integrity

Coding agents and developers MUST:

- inspect existing components before creating new ones;
- reuse shared components when appropriate;
- use design tokens instead of arbitrary values;
- avoid duplicate button/input/card variants;
- avoid changing shared behavior locally without reason;
- update shared components when a change should apply globally.

A local exception MUST NOT silently become a second design system.

---

# 38. Framework Independence

This standard does not require:

- React;
- Vue;
- Svelte;
- vanilla JavaScript;
- Flutter;
- SwiftUI;
- Jetpack Compose;
- Electron;
- Tauri;
- any specific UI framework.

The standard governs how the product should **feel and behave**.

Implementation technology may differ.

Semantic roles SHOULD remain recognizable across frameworks.

---

# 39. Coding Agent Rules

Before implementing or modifying UI, a coding agent MUST:

1. Read this `UX_UI.md`.
2. Read project-specific requirements relevant to the task.
3. Inspect existing shared components.
4. Inspect existing design tokens.
5. Reuse the existing visual system.
6. Determine the current project UI profile.
7. Check whether the requested change introduces a new visual convention.
8. Avoid redesigning unrelated surfaces.

The agent MUST NOT:

- invent arbitrary colors;
- introduce a second typography system;
- create unnecessary one-off component variants;
- use inconsistent spacing without reason;
- bypass shared components without reason;
- redesign unrelated screens during a scoped task;
- replace established interaction patterns merely for novelty;
- silently diverge from documented project overrides.

When a new convention is genuinely needed, it SHOULD be implemented at the correct shared level.

---

# 40. UI Implementation Workflow

For UI changes, prefer this sequence:

```text
Understand task
↓
Read UX_UI.md
↓
Inspect current screen and shared components
↓
Identify existing pattern to reuse
↓
Implement scoped change
↓
Validate all relevant component states
↓
Validate loading / empty / error behavior if applicable
↓
Validate responsive / resize behavior if applicable
↓
Validate keyboard and accessibility basics
↓
Run UI quality gate
```

Do not redesign unrelated screens during implementation.

---

# 41. UI Quality Gate

Before considering a UI task complete, review the relevant checks.

## Visual Language

- [ ] Does the screen feel consistent with Tinix?
- [ ] Is the visual hierarchy clear?
- [ ] Is brand orange used intentionally?
- [ ] Is decorative complexity restrained?
- [ ] Is whitespace doing useful grouping work?

## Typography

- [ ] Are typography roles consistent?
- [ ] Is display typography used selectively?
- [ ] Is dense operational text using the UI font?

## Tokens

- [ ] Are semantic color tokens used?
- [ ] Is spacing based on the shared scale?
- [ ] Are radius and elevation consistent?
- [ ] Were arbitrary one-off values avoided where practical?

## Components

- [ ] Are existing shared components reused?
- [ ] Are primary/secondary/destructive actions clearly differentiated?
- [ ] Are interactive states implemented?
- [ ] Are icon-only actions accessible?

## Layout

- [ ] Does the layout match the product type?
- [ ] Is the app shell consistent?
- [ ] Does resizing/responsiveness remain usable?
- [ ] Is dense information still readable?
- [ ] Is nested-card usage restrained?

## Forms

- [ ] Are labels visible?
- [ ] Are errors near the relevant controls?
- [ ] Is loading / double-submit handled?
- [ ] Are defaults and help text reasonable?

## System States

- [ ] Is loading handled?
- [ ] Is empty state useful?
- [ ] Are errors actionable?
- [ ] Is partial success represented correctly?
- [ ] Are long-running operations visibly progressing?
- [ ] Is save/autosave state clear where relevant?

## Interaction

- [ ] Are destructive actions clearly distinguished?
- [ ] Is confirmation used only where appropriate?
- [ ] Is undo preferred for reversible actions?
- [ ] Are overlays used for the correct purpose?
- [ ] Are interactions predictable?

## Accessibility

- [ ] Are focus states visible?
- [ ] Are controls keyboard reachable where relevant?
- [ ] Are labels and accessible names present?
- [ ] Is color not the sole carrier of state?
- [ ] Is contrast sufficient?
- [ ] Is motion respectful of reduced-motion preferences?

## Performance

- [ ] Are large datasets handled efficiently?
- [ ] Does the UI avoid appearing frozen?
- [ ] Are expensive operations communicated clearly?

## Consistency

- [ ] Has no second ad-hoc design system appeared?
- [ ] Are project-specific deviations documented?
- [ ] Does the new UI match the rest of the product family?

Significant inconsistencies SHOULD be fixed before the task is considered complete.

---

# 42. Design Review Principles

When reviewing UI, evaluate in this order:

```text
1. Task clarity
2. Interaction correctness
3. Information hierarchy
4. System-state communication
5. Consistency with existing patterns
6. Accessibility
7. Visual refinement
8. Decorative polish
```

Do not prioritize visual polish over a broken workflow.

---

# 43. Anti-Patterns

Avoid the following unless there is a documented product requirement:

- every section inside a card;
- every action styled as primary;
- excessive orange;
- excessive shadows;
- excessive gradients;
- modal dialogs for ordinary notifications;
- giant full-screen spinners for small actions;
- placeholder-only form labels;
- icon-only actions without accessible names;
- hidden error states;
- hidden save failures;
- ambiguous `OK` / `Process` / `Execute` labels;
- multiple competing navigation models;
- unbounded DOM rendering for large datasets;
- UI dependent on hover for essential behavior;
- decorative motion that slows productivity;
- local components that duplicate existing shared components;
- hard-coded visual values that drift from the design system.

---

# 44. Final Principle

Tinix applications do not need to look identical.

They MUST feel related.

A user moving from one Tinix product to another should recognize:

- the typography;
- the warmth;
- the spacing discipline;
- the restrained use of color;
- the interaction hierarchy;
- the semantic feedback;
- the clarity of system state;
- the calm visual character;
- the design-system discipline.

That consistency is the purpose of this document.
