"""Audit background roles across all app-owned XAML and generated UI sites."""
from pathlib import Path
import json
import runpy
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "Scriblucent"
WPF = "{http://schemas.microsoft.com/winfx/2006/xaml/presentation}"
X = "{http://schemas.microsoft.com/winfx/2006/xaml}"
LEGACY = ("FeatureWindowPresentation", "FeatureFrameBackground", "FeatureStageBackground",
          "WorkspaceHeaderWash", "WorkspaceTabBackground", "WorkspaceTabEdge", "HistorySummaryChip")


def audit():
    documents, shells, backgrounds, findings = [], [], [], []
    component_roots = {}
    for path in sorted(APP.rglob("*")):
        if path.suffix not in (".cs", ".xaml") or {"bin", "obj"} & set(path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        for key in LEGACY:
            if key in text:
                findings.append(f"{path.relative_to(ROOT)} retains {key}")
        if path.suffix != ".xaml":
            continue
        root = ET.fromstring(text)
        file = path.relative_to(ROOT).as_posix()
        if root.tag in (WPF + "Window", WPF + "UserControl"):
            documents.append(file)
            component_roots[file] = root
        for node in root.iter():
            if node.tag.endswith("}FeatureWindowShell"):
                if "Presentation" in node.attrib or root.get("Background") not in (None, "{DynamicResource WorkspaceBackdrop}"):
                    findings.append(f"{file} does not use the shared canvas")
            if node.get("Background"):
                backgrounds.append({"file": file, "element": node.tag.split("}")[-1],
                                    "name": node.get(X + "Name"), "background": node.get("Background")})
    # Shared UserControls can host the shell on behalf of multiple actual windows.
    # Follow those references instead of counting each template declaration as a window.
    components = {root.get(X + "Class", "").split(".")[-1]: root
                  for root in component_roots.values() if root.tag == WPF + "UserControl"}
    def uses_shell(root, seen=frozenset()):
        for node in root.iter():
            name = node.tag.split("}")[-1]
            if name == "FeatureWindowShell":
                return True
            if name in components and name not in seen and uses_shell(components[name], seen | {name}):
                return True
        return False
    for file, root in component_roots.items():
        if root.tag == WPF + "Window" and uses_shell(root):
            shells.append(file)
            if root.get("Background") != "{DynamicResource WorkspaceBackdrop}":
                findings.append(f"{file} does not use the shared canvas")
    if len(shells) != 13:
        findings.append(f"Expected 13 feature windows; found {len(shells)}")
    themes = {}
    for theme in ("Light", "Dark"):
        root = ET.parse(APP / "Themes" / (theme + "Theme.xaml")).getroot()
        resources = {node.get(X + "Key"): node for node in root if node.get(X + "Key")}
        def color(key):
            node = resources[key]
            value = node.get("Color") or (node.text or "").strip()
            if value.startswith("{StaticResource "):
                return color(value[16:-1])
            return value
        roles = {key: color(key) for key in ("SidebarBackground", "WorkspaceBackdrop", "WindowBackground", "CardBackground", "InputSurface", "InputSurfaceInset",
                                             "NoteCardBackground", "FlashcardCardBackground", "RichTextBoxBackground")}
        for key in ("NoteCardBackground", "FlashcardCardBackground", "RichTextBoxBackground"):
            if roles[key] != roles["CardBackground"]:
                findings.append(f"{theme}/{key} differs from the content surface")
        themes[theme] = roles
    inventory = runpy.run_path(str(ROOT / "tools" / "audit-ui.py"))["inventory"]()
    return {"sourceCoverageOnly": True, "screens": documents, "featureWindows": shells,
            "themes": themes, "backgroundDeclarations": backgrounds,
            "generatedUiSites": inventory["generatedControls"], "findings": findings}


if __name__ == "__main__":
    result = audit()
    output = ROOT / "artifacts" / "motion" / "workspace-frames"
    output.mkdir(parents=True, exist_ok=True)
    (output / "source-audit.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    lines = ["# Surface-role source audit", "",
             f"Inspected {len(result['screens'])} app-owned screens, {len(result['featureWindows'])} feature windows, "
             f"{len(result['backgroundDeclarations'])} explicit background declarations and {len(result['generatedUiSites'])} generated native-control sites.", "",
             "This is source coverage. Runtime captures and interaction checks have their own documented limits.", "",
             "## Theme roles", "", "| Resource | Light | Dark |", "| --- | --- | --- |"]
    for key, value in result["themes"]["Light"].items():
        lines.append(f"| {key} | {value} | {result['themes']['Dark'][key]} |")
    lines += ["", "## Feature windows", ""] + [f"- {file}: shared neutral surround and rounded workspace frame." for file in result["featureWindows"]]
    lines += ["", "## Deliberate content exceptions", "",
              "Printed paper/export documents retain white. Colour swatches and spectra display the actual selected colour. "
              "Image captions/viewers use contrast backdrops for media. Flip snapshots retain their rendering backdrop. "
              "Confetti, empty-state illustrations and the About logo halo are artwork. These are content roles, not alternate workspace themes.", "",
              "## Findings", ""] + (result["findings"] or ["No legacy presentation resources or mismatched content-role colours remain in production source."])
    (output / "source-audit.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Screens: {len(result['screens'])}; feature windows: {len(result['featureWindows'])}; "
          f"backgrounds: {len(result['backgroundDeclarations'])}; generated sites: {len(result['generatedUiSites'])}; findings: {len(result['findings'])}")
    if result["findings"]:
        raise SystemExit("\n".join(result["findings"]))
