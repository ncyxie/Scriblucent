"""Read-only coverage inventory for the colored-surface pass. Run from the repository root."""
from pathlib import Path
import argparse
import json
import re
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--output', default='artifacts/motion/colored-surfaces/inventory.json')
parser.add_argument('--include-neutral', action='store_true', help='Include quiet canvases, neutral controls and raised objects.')
args = parser.parse_args()
root = Path('Scriblucent')
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'
files = [p for p in root.rglob('*.xaml') if not any(v in ('bin', 'obj') for v in p.parts)]
trees = {p: ET.parse(p).getroot() for p in files}
styles = {}
def local(value): return value.rsplit('}', 1)[-1]
for tree in trees.values():
    for e in tree.iter():
        if local(e.tag) == 'Style' and e.get(X + 'Key'):
            own = next((c.get('Value') for c in e if local(c.tag) == 'Setter' and c.get('Property') == 'controls:SurfaceMaterial.Kind'), None)
            styles[e.get(X + 'Key')] = (own, e.get('BasedOn', ''))
def inherited(style, seen=None):
    seen = set() if seen is None else seen
    match = re.search(r'\{StaticResource ([^}]+)}', style)
    if not match or match[1] in seen or match[1] not in styles: return None
    seen.add(match[1]); own, base = styles[match[1]]
    return own or inherited(base, seen)
def material(e, parents):
    own = next((v for k,v in e.attrib.items() if local(k) == 'SurfaceMaterial.Kind'), None)
    if own: return own
    inline = next((c for c in e if local(c.tag) == 'Border.Style'), None)
    if inline is not None:
        style = next((c for c in inline if local(c.tag) == 'Style'), None)
        if style is not None:
            own = next((c.get('Value') for c in style if local(c.tag) == 'Setter' and c.get('Property') == 'controls:SurfaceMaterial.Kind'), None)
            if own: return own
    if e in parents:
        own = next((c.get('Value') for c in parents[e] if local(c.tag) == 'Setter' and c.get('Property') == 'controls:SurfaceMaterial.Kind'), None)
        if own: return own
        if local(parents[e].tag) == 'Style':
            preset = inherited(parents[e].get('BasedOn',''))
            if preset: return preset
    return inherited(e.get('Style', ''))
def colored_key(key):
    return bool(re.search(r'Accent|Chip(?:Background|Border|Foreground)|Selection|Selected|MassSelectBoxChecked|SavedFeedback|MaterialCalendar|MaterialMarker|HoverSurface|ControlHover|DropTarget', key)) or args.include_neutral and key.endswith(('Background', 'Surface'))

entries = []
for path, tree in trees.items():
    parents = {child: parent for parent in tree.iter() for child in parent}
    for e in tree.iter():
        tag = local(e.tag)
        for attribute, value in e.attrib.items():
            attribute = local(attribute)
            prop = e.get('Property', attribute) if tag == 'Setter' else attribute
            matches = re.findall(r'\{(?:Dynamic|Static)Resource ([^}]+)}', value)
            tokens = [m for m in matches if colored_key(m)]
            literal = re.findall(r'#[0-9A-Fa-f]{6,8}\b', value)
            if not tokens and not literal and not (args.include_neutral and prop in ('Background', 'Fill')): continue
            if attribute in ('Key', 'Name', 'Kind', 'Style', 'BasedOn'): continue
            preset = material(e, parents)
            if (path.parent.name == 'Themes' and (not args.include_neutral or path.name in ('LightTheme.xaml', 'DarkTheme.xaml'))) or path.name == 'Tokens.xaml':
                decision, rule = 'keep', 'Semantic palette/established directional light; surfaces consume an additional material recipe.'
            elif (prop == 'Background' and any(t.endswith('Foreground') or t.endswith('SelectionBorder') for t in tokens)) or prop in ('Foreground', 'Stroke', 'BorderBrush', 'HighlightEdge', 'controls:InteractionSurface.HighlightEdge') or 'Foreground' in prop:
                decision, rule = 'keep', 'Solid readable ink or existing functional outline; do not turn glyphs/text into material.'
            elif preset == 'None' and prop in ('Background', 'Fill'):
                decision, rule = 'keep', 'Explicit quiet/transparent/content role.'
            elif preset and prop in ('Background', 'Fill'):
                decision, rule = 'refine', preset
            elif 'MaterialCalendarSelectedFill' in tokens or 'MaterialMarkerEdge' in tokens:
                decision, rule = 'replace', 'Shared compact selected-calendar material.'
            elif path.name in ('ColorPickerDialog.xaml', 'SketchDialog.xaml') or prop == 'Tag':
                decision, rule = 'keep', 'Exact content/swatch values or drawing/preview assets.'
            elif path.name in ('FlashcardsPreviewWindow.xaml', 'QuizModeWindow.xaml') and literal:
                decision, rule = 'keep', 'Celebration artwork; preserve the expressive particle palette.'
            elif tag in ('GradientStop', 'Path', 'Polygon', 'GeometryDrawing', 'AppIcon') or prop in ('Color', 'Brush'):
                decision, rule = 'keep', 'Existing directional paint, artwork, or solid glyph.'
            elif prop in ('Fill', 'HighlightWash', 'HoverBackground', 'PressedBackground') or prop.startswith(('controls:InteractionSurface.', 'InteractionSurface.')):
                decision, rule = 'keep', 'State paint is composed by the owning material/interaction controller.'
            elif tag not in ('Border', 'SurfaceMaterialBorder', 'Button', 'InteractionSurface', 'Setter'):
                decision, rule = 'keep', 'Native selection/viewport background; the bounded face receives material separately.'
            elif path.name in ('NoteCardControl.xaml', 'FlashcardImageViewerWindow.xaml', 'PrintPreviewWindow.xaml') and literal:
                decision, rule = 'keep', 'Shadow/scrim or paper ink; preserve the established elevation/content treatment.'
            elif args.include_neutral and prop == 'Background' and value in ('Transparent', '{x:Null}'):
                decision, rule = 'keep', 'Transparent structural/input host; feedback belongs to its bounded face.'
            elif args.include_neutral and prop == 'Background' and (e.get('Effect') or e.get('IsHitTestVisible') == 'False' and not list(e)):
                decision, rule = 'keep', 'Existing shadow/scrim proxy; foreground face owns material.'
            elif args.include_neutral and prop == 'Background' and (tag == 'Setter' or any(t in ('InputSurface', 'WindowBackground', 'SidebarBackground', 'FeatureFrameBackground', 'FeatureStageBackground', 'HoverBackground') for t in tokens)):
                decision, rule = 'keep', 'Quiet field, inset row or chassis token; control/object faces opt in separately.'
            elif args.include_neutral and prop == 'Background' and ('TemplateBinding' in value or '{Binding' in value):
                decision, rule = 'keep', 'Native/content background binding; foreground material belongs to its owning template.'
            elif args.include_neutral and prop == 'Background' and any(t in ('SidebarSeparator', 'BorderColor', 'SliderTrackBackground', 'ProgressTrackBackground', 'MassSelectBoxBackground', 'RichTextBoxBackground', 'AboutStageBase') for t in matches):
                decision, rule = 'keep', 'Divider, unfilled track/indicator, text canvas or established showcase backdrop.'
            elif args.include_neutral and path.name == 'PrintPreviewWindow.xaml' and value == 'White':
                decision, rule = 'keep', 'Exact white print paper; preserve paper ink and established elevation.'
            elif args.include_neutral and prop.endswith('PressedBackground'):
                decision, rule = 'keep', 'Existing state token composed by its owning interaction controller.'
            else:
                decision, rule = 'unclassified', 'Review this surface explicitly.'
            entries.append({'file': path.as_posix(), 'element': tag, 'name': e.get(X+'Name', e.get(X+'Key', '')),
                            'property': prop, 'value': value, 'decision': decision, 'rule': rule})

# Code-created surfaces and independent renderers are a separate part of the audit.
for path in root.rglob('*.cs'):
    if any(v in ('bin','obj') for v in path.parts): continue
    lines = path.read_text(encoding='utf-8-sig').splitlines()
    for i, line in enumerate(lines):
        if not re.search(r'SurfaceMaterialRaster.Draw|SetResourceReference.*(?:Accent|Chip|Selection)|FindResource.*(?:Accent|Chip|Selection)|Resolve(?:Theme|Resource)Brush.*Accent|#[0-9A-Fa-f]{6,8}', line): continue
        around = '\n'.join(lines[max(0,i-4):i+5])
        if 'SurfaceMaterialRaster.Draw' in line:
            decision, rule = 'refine', 'Shared WPF/Skia material palette, with theme/version-aware tile signatures.'
        elif 'SurfaceMaterial.SetKind' in around:
            decision, rule = 'refine', 'Explicit preset on a code-created surface.'
        elif path.name == 'ModernMessageDialog.xaml.cs':
            decision, rule = 'refine', 'IconBackground is consumed by the tinted production dialog face.'
        elif path.name == 'QuizModeWindow.xaml.cs' and 'AccentDangerSubtle' in around:
            decision, rule = 'refine', 'Flag button selects its tinted material together with its semantic state.'
        elif 'Rendering' in path.parts:
            decision, rule = 'keep', 'Solid renderer inputs, content colors and existing selection outlines; material is applied at audited fill paths.'
        else:
            decision, rule = 'keep', 'Solid ink, stored user/content colors, illustration artwork, or functional editor grip/drop indicator.'
        entries.append({'file': path.as_posix(), 'line': i+1, 'value': line.strip(), 'decision': decision, 'rule': rule})

report = {'counts': {decision: sum(e['decision']==decision for e in entries) for decision in ('keep','refine','replace','unclassified')}, 'entries': entries}
output = Path(args.output); output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
print(json.dumps(report['counts']))
for entry in entries:
    if entry['decision'] == 'unclassified': print(entry['file'], entry.get('name',''), entry.get('property',''), entry['value'])
raise SystemExit(1 if report['counts']['unclassified'] else 0)
