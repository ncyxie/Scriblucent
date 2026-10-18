using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Services;
using Scriblucent.ViewModels;

namespace Scriblucent.Behaviors;

public enum CardSurfaceMotionProfile
{
    Listing,
    GroupSlide,
    NoteCard
}

/// <summary>
/// Vsync-aligned enter and hover motion for dashboard and note cards.
/// </summary>
public static class CardSurfaceMotionBehavior
{
    private const int EnterBurstWindowMs = 450;
    private const int NewCardAnimationWindowMs = 2500;
    private static long _lastAddTimestampMs;
    private static int _rapidAddCount;
    private static Guid? _lastAddedNoteId;

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(State),
        typeof(CardSurfaceMotionBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(CardSurfaceMotionBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty ProfileProperty = DependencyProperty.RegisterAttached(
        "Profile",
        typeof(CardSurfaceMotionProfile),
        typeof(CardSurfaceMotionBehavior),
        new PropertyMetadata(CardSurfaceMotionProfile.Listing));

    public static readonly DependencyProperty ShadowElementNameProperty = DependencyProperty.RegisterAttached(
        "ShadowElementName",
        typeof(string),
        typeof(CardSurfaceMotionBehavior),
        new PropertyMetadata(string.Empty));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    public static void SetProfile(DependencyObject element, CardSurfaceMotionProfile value)
        => element.SetValue(ProfileProperty, value);

    public static CardSurfaceMotionProfile GetProfile(DependencyObject element)
        => (CardSurfaceMotionProfile)element.GetValue(ProfileProperty);

    public static void SetShadowElementName(DependencyObject element, string value)
        => element.SetValue(ShadowElementNameProperty, value);

    public static string GetShadowElementName(DependencyObject element)
        => (string)element.GetValue(ShadowElementNameProperty);

    public static void RegisterNoteAdded(Guid noteId)
    {
        var now = Environment.TickCount64;
        if (now - _lastAddTimestampMs < EnterBurstWindowMs)
            _rapidAddCount++;
        else
            _rapidAddCount = 1;

        _lastAddTimestampMs = now;
        _lastAddedNoteId = noteId;
    }

    public static bool IsInBurstAddWindow()
        => _rapidAddCount > 2
            && Environment.TickCount64 - _lastAddTimestampMs < EnterBurstWindowMs;

    public static bool IsPlayingEnterAnimation(FrameworkElement element)
        => IsNewlyAddedCard(element) && !ShouldUseBurstEnter();

    public static void Cleanup(FrameworkElement element)
    {
        Detach(element);
        UiMotion.Cleanup(element);
        if (GetProfile(element) == CardSurfaceMotionProfile.Listing)
            UiMotion.Cleanup(ResolveListingMotionSurface(element));
    }

    /// <summary>
    /// Re-applies hover or rest visuals after enter motion when the pointer is already over the card.
    /// </summary>
    public static void SyncPointerState(FrameworkElement element)
    {
        if (!GetIsEnabled(element))
            return;

        if (CollectionSelectionFrame.For(element) is { } selectionFrame)
        {
            // Arrival/reflow completion may have changed an inner face transform.
            // Settle the decoration once; hover itself moves the whole frame.
            selectionFrame.RefreshFace(); selectionFrame.SyncHover(); return;
        }
        PlayHover(element, element.IsMouseOver);
    }

    /// <summary>
    /// Immediately returns hover scale/shadow to rest. Used when scroll suppresses hit-testing so
    /// <see cref="UIElement.MouseLeave"/> never fires on cards that were hovered when motion began.
    /// </summary>
    public static void ForceRestHoverState(FrameworkElement element)
    {
        if (!GetIsEnabled(element))
            return;
        if (CollectionSelectionFrame.For(element) is { } selectionFrame) { selectionFrame.ResetHover(); return; }

        var profile = GetProfile(element);
        if (profile == CardSurfaceMotionProfile.GroupSlide)
            return;

        var hoverTarget = ResolveHoverTarget(element, profile);

        UiMotion.Stop(UiMotion.Channel(hoverTarget, MotionChannel.Interaction));

        var translate = UiMotion.ChannelTranslate(hoverTarget, MotionChannel.Interaction);
        // Listing rest size is always 1 (0.88 is enter start only). Hover uses lift, not scale.

        translate.X = 0;
        translate.Y = 0;

        ClearLiveShadowEffect(hoverTarget);
        SurfaceLight.Reset(hoverTarget);
        if (TryResolveBakedShadowPlate(hoverTarget, element, out var plate)
            && plate is not CardShadowLayer { FollowsFace: true }
            && !IsVisualDescendantOf(plate, hoverTarget))
        {
            UiMotion.Stop(UiMotion.Channel(plate, MotionChannel.Interaction));
            var plateTranslate = UiMotion.ChannelTranslate(plate, MotionChannel.Interaction);
            plateTranslate.X = 0;
            plateTranslate.Y = 0;
            plate.Opacity = .72;
        }
    }

    /// <summary>
    /// Re-attaches hover handlers after virtualization recycle (Unloaded detaches without IsEnabled changing).
    /// </summary>
    public static void EnsureInteractive(FrameworkElement element)
    {
        if (!GetIsEnabled(element))
            return;

        if (GetOrCreateState(element).IsAttached)
            return;

        Attach(element);
    }

    /// <summary>
    /// Ensures a dashboard listing or note card is visible when enter motion is not active.
    /// Safe to call after container generation or layout refreshes.
    /// </summary>
    public static void EnsureCardVisible(FrameworkElement element)
    {
        if (!GetIsEnabled(element))
            return;

        EnsureInteractive(element);

        if (DashboardMotionEngine.IsPendingEnter(element))
        {
            EnsurePendingEnterVisible(element);
            return;
        }

        if (DashboardMotionEngine.ShouldDeferForcedVisibility(element))
            return;

        if (DashboardMotionEngine.ShouldPreserveReflowTransform(element))
            return;

        switch (GetProfile(element))
        {
            case CardSurfaceMotionProfile.Listing:
                if (NeedsListingVisibilityRepair(element))
                    PlayListingEnterImmediate(element);
                else
                    EnsureListingRestState(element);

                break;

            case CardSurfaceMotionProfile.NoteCard:
                if (NeedsNoteCardVisibilityRepair(element))
                    ResetNoteCardMotionState(element);
                break;
        }
    }

    /// <summary>
    /// Restores hover handlers and baseline visuals for a realized virtualized card.
    /// Does not cancel active reflow or enter motion on the layout root.
    /// </summary>
    public static void PrepareRecycledListingCard(FrameworkElement element)
    {
        if (!GetIsEnabled(element) || GetProfile(element) != CardSurfaceMotionProfile.Listing)
            return;

        PrepareRecycledCard(element);
    }

    public static void PrepareRecycledNoteCard(FrameworkElement element)
    {
        if (!GetIsEnabled(element) || GetProfile(element) != CardSurfaceMotionProfile.NoteCard)
            return;

        PrepareRecycledCard(element);
    }

    private static void PrepareRecycledCard(FrameworkElement element)
    {
        EnsureInteractive(element);

        if (DashboardMotionEngine.IsPendingEnter(element))
        {
            EnsurePendingEnterVisible(element);
            return;
        }

        if (element is NoteCardControl noteCard)
            noteCard.ClearDragVisualState();

        if (DashboardMotionEngine.ShouldDeferForcedVisibility(element))
            return;

        if (DashboardMotionEngine.ShouldPreserveReflowTransform(element))
            return;

        switch (GetProfile(element))
        {
            case CardSurfaceMotionProfile.Listing:
                EnsureListingRestState(element);
                break;

            case CardSurfaceMotionProfile.NoteCard:
                if (NeedsNoteCardVisibilityRepair(element))
                    ResetNoteCardMotionState(element);
                break;
        }

        if (!ShouldSuppressShadowMotion(element))
            SyncPointerState(element);
    }

    private static void EnsurePendingEnterVisible(FrameworkElement element)
    {
        // Pending cards belong to the dashboard entrance, including the period
        // before its clock starts. Do not let recycling reveal that held slot.
        if (DashboardMotionEngine.TryPrepareEnterState(element))
            return;

        if (DashboardMotionEngine.ShouldDeferForcedVisibility(element))
            return;

        if (DashboardMotionEngine.ShouldPreserveReflowTransform(element))
            return;

        switch (GetProfile(element))
        {
            case CardSurfaceMotionProfile.Listing:
                if (DashboardMotionEngine.TryPrepareEnterState(element))
                    return;

                if (NeedsListingVisibilityRepair(element))
                    PlayListingEnterImmediate(element);
                break;

            case CardSurfaceMotionProfile.NoteCard:
                if (DashboardMotionEngine.TryPrepareEnterState(element))
                    return;

                SetNoteCardVisibleRestState(element);
                break;
        }
    }

    private static void SetNoteCardVisibleRestState(FrameworkElement element)
    {
        UiMotion.Stop(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
        element.Opacity = 1;
        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = true;
    }

    /// <summary>
    /// Forces realized dashboard cards visible, bypassing enter-hold motion state.
    /// </summary>
    public static void ForceListingVisible(FrameworkElement element)
    {
        if (!GetIsEnabled(element) || DashboardMotionEngine.ShouldDeferForcedVisibility(element))
            return;

        switch (GetProfile(element))
        {
            case CardSurfaceMotionProfile.Listing:
                if (IsMassSelectActive(element) || !DashboardMotionEngine.TryPrepareEnterState(element))
                    PlayListingEnterImmediate(element);
                break;

            case CardSurfaceMotionProfile.NoteCard:
                UiMotion.Stop(element);
                var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                translate.X = 0;
                translate.Y = 0;
                element.Opacity = 1;
                element.Visibility = Visibility.Visible;
                element.IsHitTestVisible = true;
                break;
        }
    }

    public static void PlayGroupEnter(FrameworkElement element, Action? onComplete = null)
    {
        if (!GetIsEnabled(element))
        {
            onComplete?.Invoke();
            return;
        }

        if (GetProfile(element) != CardSurfaceMotionProfile.GroupSlide)
        {
            onComplete?.Invoke();
            return;
        }

        if (UiMotion.PrefersReducedMotion)
        {
            PlayEnterImmediate(element);
            onComplete?.Invoke();
            return;
        }

        UiMotion.Stop(element);
        element.Opacity = 0;
        var (groupScale, groupTranslate) = UiMotion.EnsureScaleTranslateGroup(element);
        groupTranslate.X = 0;
        groupTranslate.Y = UiMotion.GroupEnterLiftY;
        groupScale.ScaleX = groupScale.ScaleY = 1;

        UiMotion.Run(
            element,
            new[]
            {
                UiMotion.Track(UiMotion.GroupEnterMs, UiMotion.OutSine, 0, 1, value => element.Opacity = value),
                UiMotion.Track(UiMotion.GroupEnterMs, UiMotion.InOutSine, UiMotion.GroupEnterLiftY, 0, value => groupTranslate.Y = value)
            }, onComplete);
    }

    public static void PlayExit(FrameworkElement element, Action? onComplete = null)
    {
        if (!GetIsEnabled(element))
        {
            onComplete?.Invoke();
            return;
        }

        var profile = GetProfile(element);
        UiMotion.Cleanup(element);
        var durationMs = UiMotion.DeleteMs;

        switch (profile)
        {
            case CardSurfaceMotionProfile.GroupSlide:
                durationMs = UiMotion.GroupExitMs;
                var (groupExitScale, groupExitTranslate) = UiMotion.EnsureScaleTranslateGroup(element);
                groupExitScale.ScaleX = groupExitScale.ScaleY = 1;
                UiMotion.Run(
                    element,
                    new[]
                    {
                        UiMotion.Track(durationMs, UiMotion.InCubic, element.Opacity, 0, value => element.Opacity = value),
                        UiMotion.Track(durationMs, UiMotion.InCubic, groupExitTranslate.Y, UiMotion.GroupEnterLiftY, value => groupExitTranslate.Y = value)
                    },
                    onComplete);
                break;

            case CardSurfaceMotionProfile.NoteCard:
                var (exitScale, exitTranslate) = UiMotion.EnsureScaleTranslateGroup(element);
                element.Opacity = 1;
                exitTranslate.X = 0;
                exitTranslate.Y = 0;
                exitScale.ScaleX = 1;
                exitScale.ScaleY = 1;
                UiMotion.Run(
                    element,
                    new[]
                    {
                        UiMotion.Track(durationMs, UiMotion.InCubic, 1, 0, value => element.Opacity = value),
                        UiMotion.Track(durationMs, UiMotion.InCubic, 1, UiMotion.EnterScale, value =>
                        {
                            exitScale.ScaleX = value;
                            exitScale.ScaleY = value;
                        })
                    },
                    onComplete);
                break;

            default:
                var (scale, _) = UiMotion.EnsureScaleTranslateGroup(element);
                element.Opacity = 1;
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                UiMotion.Run(
                    element,
                    new[]
                    {
                        UiMotion.Track(durationMs, UiMotion.InCubic, 1, 0, value => element.Opacity = value),
                        UiMotion.Track(durationMs, UiMotion.InCubic, 1, UiMotion.EnterScale, value =>
                        {
                            scale.ScaleX = value;
                            scale.ScaleY = value;
                        })
                    },
                    onComplete);
                break;
        }
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if (e.NewValue is true)
            Attach(element);
        else
            Detach(element);
    }

    private static void Attach(FrameworkElement element)
    {
        var state = GetOrCreateState(element);
        if (state.IsAttached)
            return;

        state.LoadedHandler = (_, _) => OnCardLoaded(element);
        state.UnloadedHandler = (_, _) => Cleanup(element);
        state.MouseEnterHandler = (_, _) => PlayHover(element, isEnter: true);
        state.MouseLeaveHandler = (_, _) => PlayHover(element, isEnter: false);

        element.Loaded += state.LoadedHandler;
        element.Unloaded += state.UnloadedHandler;
        element.MouseEnter += state.MouseEnterHandler;
        element.MouseLeave += state.MouseLeaveHandler;
        state.IsAttached = true;

        if (element.IsLoaded)
            state.LoadedHandler(element, new RoutedEventArgs(FrameworkElement.LoadedEvent, element));

        ScheduleVisibilityFallback(element);
    }

    private static void OnCardLoaded(FrameworkElement element)
    {
        if (DashboardMotionEngine.IsPendingEnter(element))
        {
            EnsurePendingEnterVisible(element);
            return;
        }

        if (GetProfile(element) == CardSurfaceMotionProfile.NoteCard)
        {
            if (NeedsNoteCardVisibilityRepair(element))
                ResetNoteCardMotionState(element);
            return;
        }

        if (GetProfile(element) == CardSurfaceMotionProfile.Listing)
        {
            if (NeedsListingVisibilityRepair(element))
                PlayListingEnterImmediate(element);
            else
                EnsureListingRestState(element);

            return;
        }

        PlayEnterImmediate(element);
    }

    private static void ScheduleVisibilityFallback(FrameworkElement element)
    {
        element.Dispatcher.BeginInvoke(
            () =>
            {
                if (!GetIsEnabled(element) || DashboardMotionEngine.ShouldDeferForcedVisibility(element))
                    return;

                if (ShouldSuppressShadowMotion(element))
                    return;

                EnsureCardVisible(element);
            },
            DispatcherPriority.Loaded);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is not State state || !state.IsAttached)
            return;

        if (state.LoadedHandler != null)
            element.Loaded -= state.LoadedHandler;
        if (state.UnloadedHandler != null)
            element.Unloaded -= state.UnloadedHandler;
        if (state.MouseEnterHandler != null)
            element.MouseEnter -= state.MouseEnterHandler;
        if (state.MouseLeaveHandler != null)
            element.MouseLeave -= state.MouseLeaveHandler;

        UiMotion.Cleanup(element);
        state.IsAttached = false;
        state.LoadedHandler = null;
        state.UnloadedHandler = null;
        state.MouseEnterHandler = null;
        state.MouseLeaveHandler = null;
    }

    private static bool IsNewlyAddedCard(FrameworkElement element)
    {
        if (UiMotion.PrefersReducedMotion)
            return false;

        if (_lastAddedNoteId is not { } lastAddedId)
            return false;

        if (element.DataContext is not ViewModels.NoteCardViewModel note)
            return false;

        return note.Document.Id == lastAddedId
            && Environment.TickCount64 - _lastAddTimestampMs < NewCardAnimationWindowMs;
    }

    private static bool ShouldUseBurstEnter()
    {
        if (UiMotion.PrefersReducedMotion)
            return true;

        return _rapidAddCount > 2
            && Environment.TickCount64 - _lastAddTimestampMs < EnterBurstWindowMs;
    }

    private static void ResetNoteCardMotionState(FrameworkElement element)
    {
        if (DashboardMotionEngine.ShouldPreserveReflowTransform(element))
            return;

        UiMotion.Stop(element);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);

        if (element is NoteCardControl noteCard)
        {
            noteCard.ClearDragVisualState();

            if (DashboardMotionEngine.TryPrepareEnterState(noteCard))
                return;
        }

        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;
        element.Opacity = 1;
        element.IsHitTestVisible = true;
    }

    private static bool NeedsListingVisibilityRepair(FrameworkElement layoutRoot)
    {
        var motionSurface = ResolveListingMotionSurface(layoutRoot);
        return layoutRoot.Visibility != Visibility.Visible
            || !layoutRoot.IsHitTestVisible
            || motionSurface.Opacity < 0.95;
    }

    private static bool NeedsNoteCardVisibilityRepair(FrameworkElement element)
        => element.Visibility != Visibility.Visible
            || !element.IsHitTestVisible
            || element.Opacity < 0.95;

    private static void EnsureListingRestState(FrameworkElement layoutRoot)
    {
        if (DashboardMotionEngine.IsPendingEnter(layoutRoot))
            return;

        layoutRoot.Visibility = Visibility.Visible;
        layoutRoot.IsHitTestVisible = true;

        var motionSurface = ResolveListingMotionSurface(layoutRoot);
        if (motionSurface.Opacity < 0.95)
            motionSurface.Opacity = 1;

        // Rest size is always 1; 0.88 is enter start only. Use EnsureScaleTranslateGroup so we
        // never mutate a frozen XAML ScaleTransform (that throws and crashes the app).
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(motionSurface);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        if (!layoutRoot.IsMouseOver)
            translate.Y = 0;

        ClearLiveShadowEffect(motionSurface);
        if (!layoutRoot.IsMouseOver
            && TryResolveBakedShadowPlate(motionSurface, layoutRoot, out var plate)
            && plate is not CardShadowLayer { FollowsFace: true }
            && !IsVisualDescendantOf(plate, motionSurface))
        {
            var (_, plateTranslate) = UiMotion.EnsureScaleTranslateGroup(plate);
            plateTranslate.X = 0;
            plateTranslate.Y = 0;
        }
    }

    private static void PlayListingEnterImmediate(FrameworkElement layoutRoot)
    {
        UiMotion.Stop(layoutRoot);
        var motionSurface = ResolveListingMotionSurface(layoutRoot);

        layoutRoot.Visibility = Visibility.Visible;
        layoutRoot.IsHitTestVisible = true;
        motionSurface.Opacity = 1;

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(motionSurface);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.X = 0;
        translate.Y = 0;

        ClearLiveShadowEffect(motionSurface);
        if (TryResolveBakedShadowPlate(motionSurface, layoutRoot, out var plate)
            && plate is not CardShadowLayer { FollowsFace: true }
            && !IsVisualDescendantOf(plate, motionSurface))
        {
            var (_, plateTranslate) = UiMotion.EnsureScaleTranslateGroup(plate);
            plateTranslate.X = 0;
            plateTranslate.Y = 0;
        }
    }

    private static FrameworkElement ResolveListingMotionSurface(FrameworkElement layoutRoot)
    {
        foreach (var scope in Enum.GetValues<DashboardMotionScope>())
        {
            if (DashboardMotionCardCatalog.TryResolveCard(layoutRoot, scope, out _, out var motionSurface, out _))
                return motionSurface;
        }

        return layoutRoot;
    }

    private static void PlayEnterImmediate(FrameworkElement element)
    {
        UiMotion.Stop(element);

        switch (GetProfile(element))
        {
            case CardSurfaceMotionProfile.GroupSlide:
                element.Opacity = 1;
                var (groupScale, groupTranslate) = UiMotion.EnsureScaleTranslateGroup(element);
                groupScale.ScaleX = groupScale.ScaleY = 1;
                groupTranslate.X = groupTranslate.Y = 0;
                break;

            case CardSurfaceMotionProfile.NoteCard:
                var (noteScale, noteTranslate) = UiMotion.EnsureScaleTranslateGroup(element);
                noteScale.ScaleX = 1;
                noteScale.ScaleY = 1;
                noteTranslate.X = 0;
                noteTranslate.Y = 0;
                if (IsNewlyAddedCard(element) && !ShouldUseBurstEnter())
                    element.Opacity = 0;
                else
                    element.Opacity = 1;
                break;

            default:
                element.Opacity = 1;
                var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                translate.X = 0;
                translate.Y = 0;
                break;
        }
    }

    private static void PlayHover(FrameworkElement element, bool isEnter)
    {
        if (CollectionSelectionFrame.For(element) is { } selectionFrame) { selectionFrame.SyncHover(); return; }
        if (ShouldSuppressShadowMotion(element) || !MotionPolicy.Spatial)
            return;

        var profile = GetProfile(element);
        if (profile == CardSurfaceMotionProfile.GroupSlide)
            return;

        var hoverTarget = ResolveHoverTarget(element, profile);

        var durationMs = isEnter ? UiMotion.HoverInMs : UiMotion.HoverOutMs;
        // Lift instead of scale: RenderTransform scale upscales already-drawn ClearType
        // glyphs and inevitably softens text/icons. A translate pop stays pixel-sharp.
        var targetLift = isEnter ? UiMotion.CardHoverLiftY : 0.0;
        var translate = UiMotion.ChannelTranslate(hoverTarget, MotionChannel.Interaction);
        // Rest size is always 1 (0.88 is enter start only). Hover animates lift, not scale.


        // Prefer the shared nine-slice plate (no live DropShadowEffect). Clear any leftover
        // Effect from older sessions so hover never reintroduces per-card blur cost.
        ClearLiveShadowEffect(hoverTarget);

        if (!isEnter && translate.Y == 0
            && !NativeSurfaceMotion.IsRunning(hoverTarget, MotionChannel.Interaction))
        {
            // Recycling/visibility repair often synchronizes a card already at rest.
            // Establish its shadow directly instead of starting an empty hover-out clock.
            ForceRestHoverState(element);
            return;
        }

        var tracks = new List<NativeMotionTrack> { new(translate, TranslateTransform.YProperty, targetLift, true) };
        SurfaceLight.AddTracks(hoverTarget, tracks, isEnter);
        if (TryResolveBakedShadowPlate(hoverTarget, element, out var plate))
        {
            tracks.Add(new(plate, UIElement.OpacityProperty, isEnter ? 1 : .72));
            if (plate is not CardShadowLayer { FollowsFace: true } && !IsVisualDescendantOf(plate, hoverTarget))
            {
                UiMotion.Stop(UiMotion.Channel(plate, MotionChannel.Interaction));
                var plateTranslate = UiMotion.ChannelTranslate(plate, MotionChannel.Interaction);
                tracks.Add(new(plateTranslate, TranslateTransform.YProperty, targetLift, true));
            }
        }
        UiMotion.Stop(UiMotion.Channel(hoverTarget, MotionChannel.Interaction));
        SurfaceMotion.AnimateTracks(hoverTarget, MotionChannel.Interaction, tracks, durationMs, UiMotion.OutCubic);
    }

    private static void ClearLiveShadowEffect(FrameworkElement hoverTarget)
    {
        var shadowHost = ResolveShadowHostElement(hoverTarget);
        if (shadowHost.Effect is DropShadowEffect { IsFrozen: false })
            shadowHost.Effect = null;
        if (!ReferenceEquals(shadowHost, hoverTarget)
            && hoverTarget.Effect is DropShadowEffect { IsFrozen: false })
            hoverTarget.Effect = null;
    }

    private static FrameworkElement ResolveHoverTarget(FrameworkElement element, CardSurfaceMotionProfile profile)
        => profile == CardSurfaceMotionProfile.Listing ? ResolveListingMotionSurface(element)
            : element is NoteCardControl card && card.FindName("HoverMotionSurface") is FrameworkElement surface ? surface
            : element;

    private static bool TryResolveBakedShadowPlate(
        FrameworkElement hoverTarget,
        FrameworkElement layoutRoot,
        out FrameworkElement plate)
    {
        plate = null!;

        if (hoverTarget.Parent is Panel siblingPanel)
        {
            foreach (UIElement child in siblingPanel.Children)
            {
                if (child is CardShadowLayer layer)
                {
                    plate = layer;
                    return true;
                }
            }
        }

        if (FindDescendantCardShadowLayer(layoutRoot) is { } descendant)
        {
            plate = descendant;
            return true;
        }

        return false;
    }

    private static CardShadowLayer? FindDescendantCardShadowLayer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CardShadowLayer layer)
                return layer;

            if (FindDescendantCardShadowLayer(child) is { } nested)
                return nested;
        }

        return null;
    }

    private static bool IsVisualDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static bool IsMassSelectActive(FrameworkElement element)
        => Window.GetWindow(element)?.DataContext is MainViewModel { IsMassSelectMode: true };

    private static bool ShouldSuppressShadowMotion(FrameworkElement element)
        => DashboardScrollRenderGate.IsSmoothScrollRendering(element);

    /// <summary>Named face border when <see cref="ShadowElementNameProperty"/> is set; otherwise the element.</summary>
    private static FrameworkElement ResolveShadowHostElement(FrameworkElement element)
    {
        var shadowName = GetShadowElementName(element);
        if (!string.IsNullOrWhiteSpace(shadowName)
            && element.FindName(shadowName) is FrameworkElement namedShadowHost)
            return namedShadowHost;

        return element;
    }

    private static State GetOrCreateState(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is State existing)
            return existing;

        var state = new State();
        element.SetValue(StateProperty, state);
        return state;
    }

    private sealed class State
    {
        public bool IsAttached;
        public RoutedEventHandler? LoadedHandler;
        public RoutedEventHandler? UnloadedHandler;
        public MouseEventHandler? MouseEnterHandler;
        public MouseEventHandler? MouseLeaveHandler;
    }
}
