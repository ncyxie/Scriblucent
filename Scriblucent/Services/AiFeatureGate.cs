namespace Scriblucent.Services;

/// <summary>Single switch for Convert, model download, and AI chrome. Reads
/// <see cref="Models.AppSettings.EnableAiFeatures"/> (on by default). Tests can push an
/// override without touching the on-disk settings file.</summary>
internal static class AiFeatureGate
{
    internal const string DisabledMessage = "AI features are turned off.";

    private static bool? _enabledOverride;

    public static event EventHandler? Changed;

    public static bool IsEnabled => _enabledOverride ?? AppSettingsService.Load().EnableAiFeatures;

    public static void ThrowIfDisabled()
    {
        if (!IsEnabled)
            throw new InvalidOperationException(DisabledMessage);
    }

    public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);

    internal static IDisposable PushEnabled(bool enabled)
    {
        var previous = _enabledOverride;
        _enabledOverride = enabled;
        NotifyChanged();
        return new OverrideScope(() =>
        {
            _enabledOverride = previous;
            NotifyChanged();
        });
    }

    private sealed class OverrideScope : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public OverrideScope(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _onDispose();
        }
    }
}
