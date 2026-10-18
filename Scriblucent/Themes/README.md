# Scriblucent design system

Shared WPF resources define geometry, typography, surfaces, and interaction feedback. Use the same control roles across dashboards, editors, and dialogs. Lucide icons are rendered through `AppIcon`.

## Resources

`App.xaml` loads dictionaries in this order:

1. `Tokens.xaml`: typography, dimensions, spacing, and elevation.
2. `LightTheme.xaml` or `DarkTheme.xaml`: theme brushes, replaced by `ThemeManager`.
3. `Generic.xaml`: icon and window-shell templates.
4. `Controls/Shared.xaml`: localization and converters.
5. `Controls/Chrome.xaml`: fields and dropdowns.
6. `Controls/Calendar.xaml`: calendar controls.
7. `Controls/Buttons.xaml`: shared action styles.
8. `Controls/Dashboard.xaml`: cards and collection controls.
9. `Controls/Quiz.xaml`: quiz controls.

Application-level styles supply scrollbars, sliders, and menus. Use `DynamicResource` for theme colours and `StaticResource` for theme-independent dimensions and typography. Localize interface text in both resource files.

## Surfaces and controls

| Role | Implementation |
| --- | --- |
| Workspace | `WorkspaceFrameStyle` over the shared workspace backdrop |
| Content and nested sections | `CardBackground` and `ContentInsetSurface`; use surface contrast for separation |
| Fields | `InputSurface` or `InputSurfaceInset`, with shared Chrome styles |
| Buttons | Shared Dialog, toolbar, and creation-button styles; semantic primary/danger roles |
| Action groups | One neutral tray, transparent child targets, and `ActionDividerBrush` separators |
| Selection and focus | Shared accent rim, matching the rendered face contour |
| Floating surfaces | Shared rounded shell and cached elevation |

Neutral controls share the theme action palette. Closed non-editable dropdowns use that palette; editable dropdowns use the input palette. Popup lists use the content palette.

Separate surfaces primarily through fill and elevation. Resting hairlines serve form affordance, dark content-card edges, checkboxes, and colour swatches. Accent borders identify focus, selection, and drop destinations. Mind-map node outlines represent diagram content and retain their own styling.

Use `OverlayDialogShell` for standard dialogs and `FeatureWindowShell` for secondary workspaces. Keep header and footer actions outside a bounded scrolling body when content may exceed the owner height.

## Typography and fields

| Role | Text size | Control height |
| --- | ---: | ---: |
| Toolbar and inline actions | 14 DIP | 32 DIP |
| Menu items and form fields | 14 DIP | 36 DIP |
| Dialog actions | 14 DIP, semibold | 40 DIP |
| Editor title fields | Shared title role | 40 DIP |
| Helper text and badges | 13 / 12 DIP | Content size |

Document typography and diagram geometry are independent of interface typography.

Use `Field.ContentPadding` for ordinary fields and `Field.MultilinePadding` for multiline fields. Labels use `Field.LabelMargin`; rows use `Field.RowMargin`. An embedded text box has zero padding when its outer shell owns the inset. Do not apply padding twice through the text-view template.

Settings dropdowns stretch across their field row. Inline finite choices can use `DropdownChoiceWidthBehavior` to fit the widest localized label; selection changes must not resize the button. Keep chevrons in their disclosure column and reserve room for badges. Variable document names can use an ellipsis. Popup sizing is independent of the closed face.

## Motion and interaction

Use shared motion channels and profiles. Retarget interrupted motion from its visible state and release owned clocks, capture, and subscriptions on unloading or cancellation.

- **Automatic:** tactile button feedback, local pointer light, coordinated navigation, and purposeful staged arrivals.
- **Reduced:** stationary controls with quieter, shorter feedback; omit spatial and decorative movement.
- **Off:** settle state immediately.

Browsed cards share hover light, lift, and selection treatment. The card face, shadow, checkbox, tint, and rim move together. Editable containers keep their body quiet while their fields and commands provide feedback. Native keyboard and text-editing behavior remains available.

Card grids use body dragging or a grip without duplicate direction arrows. Linear lists and group headers provide grips with direct earlier/later arrows. Fixed dashboard sections use paired arrows. Drag, keyboard, menu, and arrow routes share ordering, boundary, and persistence logic.

## Rendering and lifecycle

- Match native and cached faces in contour, typography, colour, and DPI-aware dimensions. Selection remains independent of cached card images.
- Realize the viewport and bounded overscan. Do not construct hidden dashboard trees or load entire libraries for presentation.
- Prefer cached contours, drawing transforms, and baked elevation for repeated surfaces. Keep live blur and layout work out of scrolling and hover paths.
- Keep decorative painting clipped to its owning face. A loading cover must continue the full workspace background instead of restarting its gradient.
- Keep event-driven measurements bounded and detach observers on unload. Do not leave idle rendering subscriptions after feedback settles.

Run the [runtime UI checks](../../tools/MotionAudit/README.md) for the affected control family in both themes and relevant motion preferences.
