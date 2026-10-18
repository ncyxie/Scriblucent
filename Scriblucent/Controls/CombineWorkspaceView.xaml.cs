using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Animations;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Controls;

public interface ICombineWorkspaceHost
{
    CombineWorkspacePresentation Workspace { get; }
}

public partial class CombineWorkspaceView : UserControl
{
    private CombineWorkspacePresentation? _workspace;
    public CombineWorkspaceView()
    {
        InitializeComponent();
        Loaded += (_, _) => ObserveWorkspace();
        Unloaded += (_, _) => StopObserving();
        DataContextChanged += (_, _) => { if (IsLoaded) ObserveWorkspace(); };
    }
    private void ObserveWorkspace()
    {
        StopObserving();
        _workspace = (DataContext as ICombineWorkspaceHost)?.Workspace;
        if (_workspace is not null) _workspace.PropertyChanged += WorkspaceChanged;
    }
    private void StopObserving()
    {
        if (_workspace is not null) _workspace.PropertyChanged -= WorkspaceChanged;
        _workspace = null;
        TagFilterButton.IsChecked = false;
        SurfaceMotion.Stop(ResultSummary);
    }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CombineWorkspacePresentation.ShowSelectedOnly)) TagFilterButton.IsChecked = false;
        if (e.PropertyName == nameof(CombineWorkspacePresentation.HasSelection) && _workspace?.HasSelection == true)
            SurfaceMotion.Arrive(ResultSummary, 220);
    }
    private void SourceRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (EditorSelectionInput.IsInteractive(e.OriginalSource as DependencyObject)) return;
        if (sender is FrameworkElement row && row.DataContext is { } item && _workspace is not null)
        {
            _workspace.ToggleSourceCommand.Execute(item);
            row.Focusable = true; row.Focus(); e.Handled = true;
        }
    }
    private void SelectionCheckBox_Click(object sender, RoutedEventArgs e) => e.Handled = true;
}
