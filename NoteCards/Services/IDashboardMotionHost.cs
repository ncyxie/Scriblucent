namespace NoteCards.Services;

/// <summary>
/// Bridges collection mutations in <see cref="ViewModels.MainViewModel"/> to dashboard card motion on the visual tree.
/// </summary>
public interface IDashboardMotionHost
{
    /// <summary>Snapshots current card positions and forces layout before a collection mutation.</summary>
    void PrepareMutation(DashboardMotionScope scope);

    /// <summary>Marks an item as entering so its card stays hidden until enter motion runs.</summary>
    void PrepareItemAdd(DashboardMotionScope scope, Guid itemId);

    /// <summary>Animates neighbor reflow and the new card enter after an item was added.</summary>
    /// <param name="deferCardEnter">When true, the new card appears instantly (used with content reveal).</param>
    void CompleteItemAdd(DashboardMotionScope scope, Guid itemId, bool deferCardEnter = false);

    /// <summary>Snapshots layout once before adding many items in a single batch.</summary>
    void BeginBatchMutation(DashboardMotionScope scope);

    /// <summary>Tracks an item as entering without re-capturing layout (use inside a batch).</summary>
    void RegisterBatchItemAdd(DashboardMotionScope scope, Guid itemId);

    /// <summary>Runs one reflow and visible-only enter motion after a batch add.</summary>
    /// <param name="afterMotion">Optional work to run after motion ends and virtualization refreshes (e.g. filter refresh).</param>
    void CompleteBatchAdd(DashboardMotionScope scope, IReadOnlyList<Guid> itemIds, Action? afterMotion = null);

    /// <summary>Animates neighbor reflow after items were removed or reordered.</summary>
    /// <param name="afterMotion">Optional work after reflow completes (e.g. indicator reveal).</param>
    void CompleteMutation(DashboardMotionScope scope, Action? afterMotion = null);

    /// <summary>Plays exit motion at the source, commits the move, then enter motion at the destination.</summary>
    /// <param name="afterMotion">Optional work after exit, reflow, and enter complete.</param>
    void RequestItemMove(DashboardMotionScope scope, Guid itemId, Action commitMove, Action? afterMotion = null);

    /// <summary>Plays exit motion for multiple items, commits the move, then enter motion at destinations.</summary>
    /// <param name="afterMotion">Optional work after exit, reflow, and enter complete.</param>
    void RequestItemMoveBatch(DashboardMotionScope scope, IReadOnlyList<Guid> itemIds, Action commitMove, Action? afterMotion = null);

    /// <summary>Plays exit motion for one item, then invokes <paramref name="commitDelete"/>.</summary>
    /// <param name="afterMotion">Optional work after exit and sibling reflow (e.g. group rebuild, filters).</param>
    void RequestDelete(DashboardMotionScope scope, Guid itemId, Action commitDelete, Action? afterMotion = null);

    /// <summary>Plays exit motion for multiple items (when within the animated threshold), then commits.</summary>
    /// <param name="afterMotion">Optional work after exit and sibling reflow (e.g. group rebuild, filters).</param>
    void RequestDeleteBatch(DashboardMotionScope scope, IReadOnlyList<Guid> itemIds, Action commitDelete, Action? afterMotion = null);

    /// <summary>Reveals the dashboard card area when the first item is added to an empty dashboard.</summary>
    void PlayDashboardContentReveal(DashboardMotionScope scope);

    /// <summary>Plays exit motion for groups, then commits the collection sync.</summary>
    void RequestGroupRemovals(DashboardMotionScope scope, IReadOnlyList<Guid> groupIds, Action commitSync);

    /// <summary>Plays enter motion for a newly added group card once its container is realized.</summary>
    void ScheduleGroupCardEnter(DashboardMotionScope scope, Guid groupId);
}