using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class BulkRenameDialog : Window
{
    private readonly IReadOnlyList<RenameTarget> _targets;
    private readonly OverlayDialogAnimator _animator;
    private bool _ready, _closing, _previewPending;
    private int _generation;
    private CancellationTokenSource? _previewWork;
    private readonly DispatcherTimer _previewDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    internal IReadOnlyList<RenameChange> Changes { get; private set; } = [];
    internal BulkRenameDialog(IReadOnlyList<RenameTarget> targets)
    {
        _targets = targets; InitializeComponent();
        if (targets.Count == 1) NameBox.Text = targets[0].Name;
        _animator = ((OverlayDialogShell)Content).BindAnimator(this);
        WindowThemeService.Register(this);
        _previewDelay.Tick += (_, _) => { _previewDelay.Stop(); RefreshPreview(); };
        _ready = true; RefreshPreview();
        Closed += (_, _) => { _previewDelay.Stop(); _previewWork?.Cancel(); _generation++; };
        Loaded += (_, _) => { OverlayDialogBoundsHelper.Apply(this); UiMotion.ConfigurePopupsForWindow(this); _animator.BeginOpen(() => { NameBox.Focus(); NameBox.SelectAll(); }); };
    }
    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); OverlayDialogBoundsHelper.Apply(this); }
    private void RuleChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _generation++; _previewWork?.Cancel(); _previewDelay.Stop();
        Changes = []; _previewPending = true; ApplyButton.IsEnabled = false; RefreshMode();
        if (_targets.Count > 256) { PreviewCount.Text = LocalizationService.GetString("RenamePreviewWorking"); _previewDelay.Start(); }
        else RefreshPreview();
    }
    private void RefreshMode()
    {
        var mode = (BulkRenameMode)ModeBox.SelectedIndex;
        NameFields.Visibility = mode == BulkRenameMode.RenameTo ? Visibility.Visible : Visibility.Collapsed;
        ReplaceFields.Visibility = mode == BulkRenameMode.Replace ? Visibility.Visible : Visibility.Collapsed;
        AffixFields.Visibility = mode == BulkRenameMode.Affixes ? Visibility.Visible : Visibility.Collapsed;
        NumberFields.Visibility = mode == BulkRenameMode.Numbered ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RefreshPreview()
    {
        RefreshMode();
        int generation = _generation;
        _previewWork?.Cancel();
        using var work = new CancellationTokenSource(); _previewWork = work;
        _previewPending = true; ApplyButton.IsEnabled = false;
        var mode = (BulkRenameMode)ModeBox.SelectedIndex;
        try
        {
            if (mode == BulkRenameMode.Numbered && !int.TryParse(StartBox.Text, out _)) throw new ArgumentException("RenameStartInvalid");
            var rule = new BulkRenameRule(mode, FindBox.Text, mode switch { BulkRenameMode.RenameTo => NameBox.Text, BulkRenameMode.Numbered => BaseBox.Text, _ => ReplacementBox.Text },
                PrefixBox.Text, SuffixBox.Text, mode == BulkRenameMode.Numbered ? int.Parse(StartBox.Text) : 1);
            var preview = _targets.Count > 256
                ? await Task.Run(() => BulkRename.Preview(_targets, rule, work.Token), work.Token)
                : BulkRename.Preview(_targets, rule);
            if (_closing || generation != _generation) return;
            Changes = preview.Where(change => change.Before != change.After).ToArray();
            PreviewList.ItemsSource = preview.Take(60).ToArray();
            PreviewCount.Text = string.Format(LocalizationService.GetString("RenamePreviewCount"), Changes.Count, Math.Min(60, preview.Count), preview.Count);
            ApplyButton.IsEnabled = Changes.Count > 0; Validation.Show(null);
        }
        catch (OperationCanceledException) { }
        catch (ArgumentException error)
        {
            if (_closing || generation != _generation) return;
            Changes = []; ApplyButton.IsEnabled = false;
            PreviewList.ItemsSource = _targets.Take(60).Select(target => new RenameChange(target.Id, target.Name, target.Name)).ToArray();
            PreviewCount.Text = string.Format(LocalizationService.GetString("RenamePreviewCount"), 0, Math.Min(60, _targets.Count), _targets.Count);
            Validation.Show(error.Message == "RenameFindRequired" || (mode == BulkRenameMode.RenameTo && NameBox.Text.Length == 0) ? null : LocalizationService.GetString(error.Message));
        }
        finally
        {
            if (ReferenceEquals(_previewWork, work)) _previewWork = null;
            if (generation == _generation) _previewPending = false;
        }
    }
    private void ApplyClick(object sender, RoutedEventArgs e) { if (!_previewPending && Changes.Count > 0) CloseDialog(true); }
    private void CancelClick(object sender, RoutedEventArgs e) => CloseDialog(false);
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; CloseDialog(false); } }
    private void CloseDialog(bool accepted)
    { if (_closing) return; _closing = true; _previewDelay.Stop(); _previewWork?.Cancel(); _generation++; _animator.BeginClose(() => { DialogResult = accepted; Close(); }); }
}
