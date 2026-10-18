# Runtime UI checks

Run from the repository root on Windows with the .NET 10 SDK. The checks below host production controls with synthetic documents or isolated fixture stores. Supply an output directory under the ignored `artifacts/` folder and one primary check flag.

```powershell
$auditOutput = Join-Path (Get-Location) 'artifacts/ui-checks/build/'
dotnet build tools/MotionAudit/MotionAudit.csproj -c Release -p:BaseOutputPath="$auditOutput"
dotnet (Join-Path $auditOutput 'Release/net10.0-windows/Scriblucent.MotionAudit.dll') artifacts/ui-checks/dropdowns --closed-dropdowns
```

Wait for the build to finish before starting the executable. Run application, test, and audit builds sequentially because they share intermediate output. A failing check returns a nonzero exit code.

## Checks

| Flag | Coverage |
| --- | --- |
| `--closed-dropdowns`, `--popup-bounds` | Closed labels, adaptive widths, popup wrapping, scrolling, and localization |
| `--field-spacing`, `--ui-fixes` | Field/caret alignment, native hit targets, switches, and control materials |
| `--wrapped-note-save` | Manual/autosave state with wrapped images and subsequent edits |
| `--surface-cards`, `--drag-shadow` | Current dashboard card faces, title bounds, rounded contours, and drag elevation |
| `--dashboard-loading`, `--dashboard-loading-geometry` | First paint, skeleton geometry, store handoff, cancellation, and retry |
| `--dashboard-chrome-regressions` | Stable logo dimensions and workspace lighting across theme changes |
| `--dashboard-toolbar`, `--dashboard-toolbar-motion` | Collection actions, toolbar layout, transitions, and motion cleanup |
| `--dashboard-groups`, `--dashboard-ordering` | Group controls, section ordering, disclosure, and compact layout |
| `--manual-order`, `--ordering-settings` | Card/list/group ordering, selected blocks, persistence, and Settings tools |
| `--dashboard-selection`, `--editor-selection`, `--selection-completion` | Logical selection, bulk commands, recycling, and exports |
| `--editor-workspace` | Tab working copies, saving, failure recovery, and closing |
| `--combine-workspace` | Shared source selection, filtering, result composition, and output |
| `--dashboard-calendar`, `--calendar-week-start` | Planner interactions, date columns, and saved week-start preference |
| `--about-layout`, `--update-dialog-shadow` | Dialog bounds, footer access, and elevation |
| `--smooth-caret` | Native input, caret motion, zoom, and fallback |

## Options

- `--software` forces WPF software rendering.
- `--dark` selects Dark for loading, toolbar, ordering, and Settings checks that otherwise default to Light. Checks that enumerate both themes need no theme option.
- Selection and manual-order checks accept `--case=Dark/Reduced`; group checks accept `--case=Light/Notes`; combine checks accept `--case=Dark/lt`.
- `--manual-order --editors-only` limits the ordering check to editors.
- `--pointer-controls` enables desktop pointer checks for manual ordering and Settings ordering. These require an interactive foreground window and move the real pointer; the fixture releases capture and restores pointer state afterward.

## Interpreting results

Each check writes its report and any captures to the chosen output directory. Captures at multiple raster scales verify drawing geometry, not monitor-DPI transitions or presented frame rates. Software rendering exercises that rendering path without certifying performance on every computer.

Test fixtures may suppress unrelated startup handlers. Check the probe implementation when assessing full application workflows. Keep generated reports, fixture stores, and captures out of source control.

Control contracts are documented in the [design-system guide](../../Scriblucent/Themes/README.md).
