using System.Windows;
using System.Windows.Controls;
using Scriblucent.Controls.Dashboard;
using Scriblucent.Icons;

namespace Scriblucent.Controls;

/// <summary>Shares the dashboard's contextual artwork with editor empty states.</summary>
public sealed class EmptyStateIllustration : Grid
{
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind), typeof(AppIconKind), typeof(EmptyStateIllustration),
        new PropertyMetadata(AppIconKind.Notes, (d, _) => ((EmptyStateIllustration)d).Refresh()));
    public AppIconKind IconKind { get => (AppIconKind)GetValue(IconKindProperty); set => SetValue(IconKindProperty, value); }
    public EmptyStateIllustration() { IsHitTestVisible = false; Refresh(); }
    private void Refresh() { Children.Clear(); Children.Add(new EmptyDashboardIllustration(IconKind)); }
}
