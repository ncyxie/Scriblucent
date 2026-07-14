using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Views.Dashboards;

public partial class MindMapsDashboardPage : UserControl
{
    private const double HoverPreviewCursorGap = 12;
    private const double HoverPreviewShadowPadLeft = 28;
    private const double HoverPreviewShadowPadTop = 28;
    private const double HoverPreviewMoveThreshold = 2;

    private Point? _lastHoverPreviewMousePosition;

    public MindMapsDashboardPage()
    {
        InitializeComponent();
    }

    private void MindMapHoverPreviewPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup { PlacementTarget: FrameworkElement target, Child: FrameworkElement child } popup)
            return;

        UpdateMindMapHoverPreviewPopupPosition(popup, target);

        if (TryGetHoverPreviewShadowPlate(child) is Border shadowPlate)
            shadowPlate.CacheMode = DpiAwareBitmapCache.Create(shadowPlate, enableClearType: false);

        UiMotion.AnimatePopupOpenDeferred(child, popup);
    }

    private void MindMapHoverPreviewPopup_Closed(object? sender, EventArgs e)
    {
        _lastHoverPreviewMousePosition = null;

        if (sender is Popup { Child: FrameworkElement child })
        {
            if (TryGetHoverPreviewShadowPlate(child) is Border shadowPlate)
                shadowPlate.CacheMode = null;

            UiMotion.PreparePopupOpenVisual(child);
        }
    }

    private void MindMapContentPreviewBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement border
            || FindHoverPreviewPopup(border) is not { IsOpen: true } popup)
            return;

        var position = e.GetPosition(border);
        if (_lastHoverPreviewMousePosition is { } last
            && Math.Abs(position.X - last.X) < HoverPreviewMoveThreshold
            && Math.Abs(position.Y - last.Y) < HoverPreviewMoveThreshold)
            return;

        _lastHoverPreviewMousePosition = position;
        UpdateMindMapHoverPreviewPopupPosition(popup, border, position);
    }

    private static void UpdateMindMapHoverPreviewPopupPosition(
        Popup popup,
        FrameworkElement anchor,
        Point? positionOnAnchor = null)
    {
        var position = positionOnAnchor ?? Mouse.GetPosition(anchor);
        var offsetX = position.X + HoverPreviewCursorGap - HoverPreviewShadowPadLeft;
        var offsetY = position.Y + HoverPreviewCursorGap - HoverPreviewShadowPadTop;

        if (popup.Child is FrameworkElement root)
            ClampHoverPreviewToWorkArea(anchor, root, ref offsetX, ref offsetY);

        popup.Placement = PlacementMode.Relative;
        popup.PlacementTarget = anchor;
        popup.HorizontalOffset = offsetX;
        popup.VerticalOffset = offsetY;
    }

    private static void ClampHoverPreviewToWorkArea(
        FrameworkElement anchor,
        FrameworkElement popupRoot,
        ref double offsetX,
        ref double offsetY)
    {
        popupRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var popupSize = popupRoot.DesiredSize;
        if (popupSize.Width <= 0 || popupSize.Height <= 0)
            return;

        var workArea = SystemParameters.WorkArea;
        var screen = anchor.PointToScreen(new Point(offsetX, offsetY));

        var x = screen.X;
        var y = screen.Y;

        if (x + popupSize.Width > workArea.Right)
            x = workArea.Right - popupSize.Width;
        if (y + popupSize.Height > workArea.Bottom)
            y = workArea.Bottom - popupSize.Height;
        if (x < workArea.Left)
            x = workArea.Left;
        if (y < workArea.Top)
            y = workArea.Top;

        var local = anchor.PointFromScreen(new Point(x, y));
        offsetX = local.X;
        offsetY = local.Y;
    }

    private static Popup? FindHoverPreviewPopup(FrameworkElement previewBorder)
    {
        var current = LogicalTreeHelper.GetParent(previewBorder) as DependencyObject
                      ?? VisualTreeHelper.GetParent(previewBorder);
        while (current is not null)
        {
            if (current is Grid grid)
            {
                for (var i = 0; i < grid.Children.Count; i++)
                {
                    if (grid.Children[i] is Popup popup)
                        return popup;
                }
            }

            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static Border? TryGetHoverPreviewShadowPlate(DependencyObject? popupChild)
    {
        if (popupChild is not Border
            || VisualTreeHelper.GetChild(popupChild, 0) is not Grid grid
            || grid.Children.Count == 0
            || grid.Children[0] is not Border shadowPlate)
            return null;

        return shadowPlate;
    }

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    private void CalendarScheduleGearButton_Click(object sender, RoutedEventArgs e)
        => Host?.CalendarScheduleGearButton_Click(sender, e);
    private void CalendarScheduledItemCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => Host?.CalendarScheduledItemCard_MouseLeftButtonUp(sender, e);
    private void CreateMindMapButton_Click(object sender, RoutedEventArgs e)
        => Host?.CreateMindMapButton_Click(sender, e);
    private void DashboardCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        => Host?.DashboardCalendar_SelectedDatesChanged(sender, e);
    private void DashboardGroupBorder_PreviewDragLeave(object sender, DragEventArgs e)
        => Host?.DashboardGroupBorder_PreviewDragLeave(sender, e);
    private void DashboardGroupBorder_PreviewDragOver(object sender, DragEventArgs e)
        => Host?.DashboardGroupBorder_PreviewDragOver(sender, e);
    private void DashboardGroupBorder_PreviewDrop(object sender, DragEventArgs e)
        => Host?.DashboardGroupBorder_PreviewDrop(sender, e);
    private void DashboardGroupColorButton_Click(object sender, RoutedEventArgs e)
        => Host?.DashboardGroupColorButton_Click(sender, e);
    private void DashboardGroupColorMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DashboardGroupColorMenuItem_Click(sender, e);
    private void DashboardGroupMenuButton_Click(object sender, RoutedEventArgs e)
        => Host?.DashboardGroupMenuButton_Click(sender, e);
    private void DashboardListing_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => Host?.DashboardListing_MouseLeftButtonUp(sender, e);
    private void DashboardListing_PreviewDragEnter(object sender, DragEventArgs e)
        => Host?.DashboardListing_PreviewDragEnter(sender, e);
    private void DashboardListing_PreviewDragLeave(object sender, DragEventArgs e)
        => Host?.DashboardListing_PreviewDragLeave(sender, e);
    private void DashboardListing_PreviewDragOver(object sender, DragEventArgs e)
        => Host?.DashboardListing_PreviewDragOver(sender, e);
    private void DashboardListing_PreviewDrop(object sender, DragEventArgs e)
        => Host?.DashboardListing_PreviewDrop(sender, e);
    private void DashboardListing_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => Host?.DashboardListing_PreviewMouseLeftButtonDown(sender, e);
    private void DashboardListing_PreviewMouseMove(object sender, MouseEventArgs e)
        => Host?.DashboardListing_PreviewMouseMove(sender, e);
    private void DashboardScrollViewer_Drop(object sender, DragEventArgs e)
        => Host?.DashboardScrollViewer_Drop(sender, e);
    private void DashboardScrollViewer_PreviewDragLeave(object sender, DragEventArgs e)
        => Host?.DashboardScrollViewer_PreviewDragLeave(sender, e);
    private void DashboardScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
        => Host?.DashboardScrollViewer_PreviewDragOver(sender, e);
    private void DashboardUngroupedDropZone_DragLeave(object sender, DragEventArgs e)
        => Host?.DashboardUngroupedDropZone_DragLeave(sender, e);
    private void DashboardUngroupedDropZone_DragOver(object sender, DragEventArgs e)
        => Host?.DashboardUngroupedDropZone_DragOver(sender, e);
    private void DashboardUngroupedDropZone_Drop(object sender, DragEventArgs e)
        => Host?.DashboardUngroupedDropZone_Drop(sender, e);
    private void DeleteMindMapMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DeleteMindMapMenuItem_Click(sender, e);
    private void DisbandDashboardGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DisbandDashboardGroupMenuItem_Click(sender, e);
    private void DuplicateMindMapMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.DuplicateMindMapMenuItem_Click(sender, e);
    private void ListingContextMenuButton_Click(object sender, RoutedEventArgs e)
        => Host?.ListingContextMenuButton_Click(sender, e);
    private void MindMapInfoMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.MindMapInfoMenuItem_Click(sender, e);
    private void MoveCalendarDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveCalendarDownButton_Click(sender, e);
    private void MoveCalendarUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveCalendarUpButton_Click(sender, e);
    private void MoveDashboardContentDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardContentDownButton_Click(sender, e);
    private void MoveDashboardContentUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardContentUpButton_Click(sender, e);
    private void MoveDashboardGroupDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardGroupDownButton_Click(sender, e);
    private void MoveDashboardGroupUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveDashboardGroupUpButton_Click(sender, e);
    private void MoveMindMapDashboardGroupsDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveMindMapDashboardGroupsDownButton_Click(sender, e);
    private void MoveMindMapDashboardGroupsUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveMindMapDashboardGroupsUpButton_Click(sender, e);
    private void MoveMindMapDashboardUngroupedDownButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveMindMapDashboardUngroupedDownButton_Click(sender, e);
    private void MoveMindMapDashboardUngroupedUpButton_Click(object sender, RoutedEventArgs e)
        => Host?.MoveMindMapDashboardUngroupedUpButton_Click(sender, e);
    private void OpenMindMapMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.OpenMindMapMenuItem_Click(sender, e);
    private void RecentMindMapButton_Click(object sender, RoutedEventArgs e)
        => Host?.RecentMindMapButton_Click(sender, e);
    private void RemoveMindMapFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.RemoveMindMapFromGroupMenuItem_Click(sender, e);
    private void RenameDashboardGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.RenameDashboardGroupMenuItem_Click(sender, e);
    private void ScheduleMindMapMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.ScheduleMindMapMenuItem_Click(sender, e);
    private void StartDashboardMassSelectMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.StartDashboardMassSelectMenuItem_Click(sender, e);
    private void ToggleCalendarSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleCalendarSectionButton_Click(sender, e);
    private void ToggleMindMapDashboardGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleMindMapDashboardGroupsSectionButton_Click(sender, e);
    private void ToggleMindMapDashboardUngroupedSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleMindMapDashboardUngroupedSectionButton_Click(sender, e);
    private void ToggleMindMapGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleMindMapGroupsSectionButton_Click(sender, e);
    private void ToggleMindMapPinMenuItem_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleMindMapPinMenuItem_Click(sender, e);
    private void ToggleRecentSectionButton_Click(object sender, RoutedEventArgs e)
        => Host?.ToggleRecentSectionButton_Click(sender, e);
}
