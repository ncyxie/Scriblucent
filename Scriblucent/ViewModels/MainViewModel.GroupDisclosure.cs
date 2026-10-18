using Scriblucent.Services;

namespace Scriblucent.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<(DashboardMotionScope Scope, Guid GroupId), GroupDisclosureState> _groupDisclosures = new();

    internal GroupDisclosureState GetGroupDisclosure(DashboardMotionScope scope, Guid groupId)
    {
        var key = (scope, groupId);
        if (_groupDisclosures.TryGetValue(key, out var state)) return state;
        var scopeKey = scope.ToString();
        var collapsed = _settings.CollapsedDashboardGroups is { } saved && saved.TryGetValue(scopeKey, out var ids) && ids?.Contains(groupId) is true;
        state = new GroupDisclosureState(!collapsed, expanded =>
        {
            var groups = _settings.CollapsedDashboardGroups ??= new();
            if (!groups.TryGetValue(scopeKey, out var closed) || closed is null) groups[scopeKey] = closed = new();
            if (expanded) closed.Remove(groupId); else closed.Add(groupId);
            if (closed.Count == 0) groups.Remove(scopeKey);
            SaveAppSettings();
        });
        _groupDisclosures.Add(key, state);
        return state;
    }
}
