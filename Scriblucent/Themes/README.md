# Scriblucent design system

Custom WPF theming — not MaterialDesign / MahApps UI / WPF-UI.
Icons: Lucide via `MahApps.Metro.IconPacks.Lucide` (`AppIcon`).

## Resource load order (`App.xaml`)

1. `Themes/Tokens.xaml` — geometry, type, space, elevation (theme-invariant)
2. `Themes/LightTheme.xaml` or `DarkTheme.xaml` — brushes (swapped by `ThemeManager`)
3. `Themes/Generic.xaml` — `AppIcon` + `OverlayDialogShell` / `FeatureWindowShell` templates
4. `Themes/Controls/Shared.xaml` — Loc + converters
5. `Themes/Controls/Chrome.xaml` — fields, TextBox, ComboBox
6. `Themes/Controls/Calendar.xaml` — shared calendar day/header/item styles
7. `Themes/Controls/Buttons.xaml` — implicit Button, Dialog*, ToolbarIcon, FAB helpers
8. `Themes/Controls/Dashboard.xaml` — tags, mass-select, section chrome
9. App-level ScrollBar / Slider / menus

## Non-negotiable rules

| Area | Rule |
|------|------|
| Accent | `AccentPrimary` (+ Hover/Pressed/Foreground) for focus, selection, primary CTAs |
| Surfaces | Main shell: `SidebarBackground` + `WindowBackground`. Feature/editors: `FeatureFrameBackground` (outer ring) + `FeatureStageBackground` (plate) + white fields |
| Inputs | Outer `Chrome*BorderStyle` + inner `EmbeddedFieldTextBoxStyle` — no local TextBox/ComboBox templates |
| Buttons | `DialogPrimary/Secondary/Danger/Warning`, `ToolbarIconButtonStyle`, FAB — implicit Button is theme-aware |
| Dialogs | `OverlayDialogShell` — `ResizeMode=NoResize`, `Radius.Overlay`, `Elevation.Dialog` |
| Feature windows | `FeatureWindowShell` — inset card on `SidebarBackground` |
| Headers | DialogHeader (icon chip + title) or WindowChromeHeader (`ChromePanelBorderStyle`) |
| Bindings | Theme color → `DynamicResource`; geometry/type/space → `StaticResource` |

## Stroke (border) contract — soft-first

**Default: no box stroke.** Separate layers with surface steps (`Sidebar` → `Window` → `Card`) and elevation (`Elevation.*` / `CardShadowLayer`). Use a 1px hairline only for form affordance, selection, or dark-mode card rest edges. Pick by **role**, not by eye. Do not set `BorderBrush` when thickness is 0.

| Role | Stroke? | How edge is defined | Examples |
|------|---------|---------------------|----------|
| A Filled action | **No** | Fill (+ `Elevation.Button` for primary/FAB) | Dialog CTAs, toolbar icons, FAB |
| B Soft chip / tag | **No** | Fill only | Tag chips, stat chips |
| C Form control | **Light: no rest stroke** | Light: shadow + stage contrast; dark: soft rest 1px; focus always accent | Fields, checkbox, radio |
| D Content card | **Soft-first** | Shadow/elevation; `NoteCardBorder` transparent light / soft dark | Listing tiles, recent, calendar items |
| E Section / nested group | **No** | Surface contrast; optional `Elevation.Field` | Dashboard sections, Settings groups |
| F Floating surface | **Soft-first** | `Elevation.*` only when elevated — no rest hairline | Dialogs, menus, popovers, feature stage |
| G Emphasis / selection | **Yes (accent)** | `NoteCardSelectionBorder` / `AccentPrimary` 1–2px | Mass-select, selected flashcard, focus |
| H Separator | **Line only** | `SidebarSeparator` | Sidebar rules |
| I Status glyph | Optional | `Accent*SubtleBorder` | Message icon chips |

**Always keep stroke:** checkboxes/radios, color swatches (empty/light colors), drop-target glow. Dark mode may still show soft card hairlines because shadows read poorly on dark UIs.

## Shells

- **Overlay dialogs:** set window content to `OverlayDialogShell`; bind animator with `((OverlayDialogShell)Content).BindAnimator(this)` (or `DialogShell.BindAnimator(this)` if named).
- **Feature windows:** wrap content in `FeatureWindowShell` with Header / Body / Footer slots.

## Do not

- Hardcode `#435CEC` / light-only chip colors in views
- Copy ComboBox/TextBox ControlTemplates into window resources
- Invent a third dialog radius or shadow
- Put decorative strokes on filled buttons, chips, or elevated section shells

## Edge treatments

| Code | Meaning |
|------|---------|
| **S** | Soft — fill/surface only, no box stroke |
| **E** | Elevation — soft + `Elevation.*` / `CardShadowLayer` |
| **R** | Rest stroke — theme hairline (`InputRestBorder`, dark card rest, `BorderColor` on checkbox/radio) |
| **A** | Accent stroke — selection/focus 1–2px |
| **L** | Line separator — 1px divider, not a box |

Mind-map node outlines represent diagram content and are separate from the application edge contract.
