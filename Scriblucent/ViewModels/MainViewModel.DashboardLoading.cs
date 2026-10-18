using System.Windows.Threading;

namespace Scriblucent.ViewModels;

public partial class MainViewModel
{
    private sealed class DashboardLoadRun
    {
        internal readonly Dispatcher Dispatcher = Dispatcher.CurrentDispatcher;
        internal readonly CancellationTokenSource Cancellation = new();
        internal CancellationToken Token => Cancellation.Token;
    }
    private readonly Dictionary<string, DashboardLoadRun> _pendingDashboardLoads = new(StringComparer.Ordinal);
    private bool _dashboardLoadsStopped;

    private DashboardLoadRun BeginDashboardLoad(string dashboard)
    {
        if (_pendingDashboardLoads.Remove(dashboard, out var old)) old.Cancellation.Cancel();
        var run = new DashboardLoadRun();
        _pendingDashboardLoads[dashboard] = run;
        if (_dashboardLoadsStopped) run.Cancellation.Cancel();
        return run;
    }

    internal void CancelPendingDashboardLoads()
    {
        _dashboardLoadsStopped = true;
        var runs = _pendingDashboardLoads.Values.ToArray();
        _pendingDashboardLoads.Clear();
        foreach (var run in runs) run.Cancellation.Cancel();
    }

    private bool IsCurrentDashboardLoad(string dashboard, DashboardLoadRun run) =>
        !_dashboardLoadsStopped && !run.Cancellation.IsCancellationRequested
        && _pendingDashboardLoads.TryGetValue(dashboard, out var current) && ReferenceEquals(current, run);

    private void CompletePreparedDashboardLoad<T>(Task<T> read, string dashboard, DashboardLoadRun run,
        Func<T, CancellationToken, Task> apply, Action markReady, Action clearInProgress)
    {
        _ = read.Exception; // Observe a superseded read failure even when its window has closed.
        if (run.Dispatcher.HasShutdownStarted) { run.Cancellation.Dispose(); return; }
        run.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (!IsCurrentDashboardLoad(dashboard, run)) return;
                var result = read.GetAwaiter().GetResult();
                // Keep failure/save guards until the replacement library is fully prepared.
                await apply(result, run.Token);
                if (!IsCurrentDashboardLoad(dashboard, run)) return;
                _dashboardLoadFailures.Remove(dashboard);
                _dashboardLoadRetriesInProgress.Remove(dashboard);
                markReady();
            }
            catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (IsCurrentDashboardLoad(dashboard, run)) ReportDashboardLoadFailure(dashboard, error);
            }
            finally
            {
                if (_pendingDashboardLoads.TryGetValue(dashboard, out var current) && ReferenceEquals(current, run))
                {
                    clearInProgress();
                    _pendingDashboardLoads.Remove(dashboard);
                }
                run.Cancellation.Dispose();
            }
        }), DispatcherPriority.Background);
    }

    private static Task DashboardLoadStep(CancellationToken cancellation, Action step) =>
        Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            step();
        }, DispatcherPriority.Background, cancellation).Task;

    // These documents are still privately owned by the load operation. Constructors and
    // normalization touch no controls/collection views; Task.Run carries the caller's culture.
    private static TModel[] PrepareLoadModels<TDocument, TModel>(IEnumerable<TDocument> documents,
        Action<TDocument> normalize, Func<TDocument, TModel> create, CancellationToken cancellation)
        where TDocument : class => documents.Where(document => document is not null).Select(document =>
        {
            cancellation.ThrowIfCancellationRequested();
            normalize(document);
            return create(document);
        }).ToArray();
}
