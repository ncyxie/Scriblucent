using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Scriblucent.Services;

/// <summary>
/// Measures the rendered document in RichTextBox viewport coordinates. Cell boxes use
/// the public content-host API; native text selection geometry needs the same narrow
/// WPF text-view access as the editor's Undo repair. Covered against the .NET 10 target.
/// </summary>
internal sealed class WpfEditorGeometry(RichTextBox editor)
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly PropertyInfo Container = typeof(TextPointer).GetProperty("TextContainer", Members)!;
    private static readonly Type ContainerType = typeof(TextPointer).Assembly.GetType("System.Windows.Documents.TextContainer")!;
    private static readonly PropertyInfo TextView = ContainerType.GetProperty("TextView", Members)!;
    private static readonly Type ViewType = typeof(TextPointer).Assembly.GetType("System.Windows.Documents.ITextView")!;
    private static readonly MethodInfo TightGeometry = ViewType.GetMethod("GetTightBoundingGeometryFromTextPositions")!;
    private static readonly PropertyInfo RenderScope = ViewType.GetProperty("RenderScope")!;
    private static readonly PropertyInfo IsValid = ViewType.GetProperty("IsValid")!;
    private static readonly ConditionalWeakTable<RichTextBox, WpfEditorGeometry> Instances = new();
    private (IContentHost Host, Visual Visual)[] _hosts = [];

    internal static WpfEditorGeometry For(RichTextBox editor)
        => Instances.GetValue(editor, item => new WpfEditorGeometry(item));

    internal Rect ElementBounds(ContentElement element)
    {
        if (element is TextElement text && !text.ContentStart.HasValidLayout) return Rect.Empty;
        if (_hosts.Length == 0 || _hosts.Any(item => !editor.IsAncestorOf(item.Visual)))
            _hosts = Hosts(editor).ToArray();
        var bounds = Rect.Empty;
        foreach (var (host, visual) in _hosts)
        {
            try
            {
                var transform = visual.TransformToAncestor(editor);
                foreach (var rectangle in host.GetRectangles(element))
                    bounds.Union(transform.TransformBounds(rectangle));
            }
            catch (ArgumentException) { /* This host belongs to another embedded element. */ }
        }
        return bounds;
    }

    internal bool TrySelection(TextPointer start, TextPointer end, out Geometry geometry)
    {
        geometry = Geometry.Empty;
        if (!TryView(start, out var view, out var scope) || !editor.IsAncestorOf(scope)) return false;
        try
        {
            if (TightGeometry.Invoke(view, [start, end]) is Geometry native)
                geometry = Transform(native, ToMatrix(scope.TransformToAncestor(editor)));
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException or ArgumentException)
        {
            // Reflow can temporarily invalidate a view between scheduling and painting.
            return false;
        }
    }

    internal static Rect SelectionBounds(TextPointer start, TextPointer end)
    {
        if (!TryView(start, out _, out var scope)) return Rect.Empty;
        DependencyObject? parent = scope;
        while (parent is not null && parent is not RichTextBox) parent = VisualTreeHelper.GetParent(parent);
        return parent is RichTextBox owner && For(owner).TrySelection(start, end, out var geometry)
            ? geometry.Bounds : Rect.Empty;
    }

    internal static Geometry Transform(Geometry geometry, Matrix matrix)
    {
        if (geometry.IsEmpty()) return Geometry.Empty;
        var copy = geometry.Clone();
        copy.Transform = new TransformGroup
        {
            Children = { copy.Transform, new MatrixTransform(matrix) }
        };
        if (copy.CanFreeze) copy.Freeze();
        return copy;
    }

    private static bool TryView(TextPointer pointer, out object view, out Visual scope)
    {
        view = TextView.GetValue(Container.GetValue(pointer))!;
        scope = view is not null ? RenderScope.GetValue(view) as Visual ?? null! : null!;
        return view is not null && scope is not null && (bool)IsValid.GetValue(view)!;
    }

    private static Matrix ToMatrix(GeneralTransform transform)
    {
        var origin = transform.Transform(new Point());
        var x = transform.Transform(new Point(1, 0));
        var y = transform.Transform(new Point(0, 1));
        return new Matrix(x.X - origin.X, x.Y - origin.Y, y.X - origin.X, y.Y - origin.Y, origin.X, origin.Y);
    }

    private static IEnumerable<(IContentHost, Visual)> Hosts(DependencyObject root)
    {
        if (root is IContentHost host && root is Visual visual) yield return (host, visual);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var nested in Hosts(VisualTreeHelper.GetChild(root, i))) yield return nested;
    }
}
