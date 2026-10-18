param([string]$Output = 'artifacts/motion')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repository
try {
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    $calls = & rg -n '(BeginAnimation|ApplyAnimationClock|ObjectAnimation|DoubleAnimation|RectAnimation|ThicknessAnimation|ColorAnimation|Storyboard|CompositionTarget\.Rendering|UiMotion\.(Run|Animate|Fade|Pulse)|StudyCelebration\.)' Scriblucent -g '*.cs' -g '*.xaml'
    $calls | Set-Content -LiteralPath (Join-Path $Output 'callsites.txt') -Encoding utf8
    $attachments = & rg -n '(MotionBehavior\.|ItemPresenceMotionBehavior\.|WindowContentEnterHelper\.|OverlayPanelAnimator\.|EditorToolbarReveal\.|SurfaceMotion\.|NativeSurfaceMotion\.|WindowMotion\.|ArrivalGroups\.|SidebarResizeMotion\.|LoadContinuityBehavior\.|ContextualStatePanel|SidebarSelectionSurface|SurfaceLight\.|SurfaceFill\.|InteractionSurface|MotionProfiles\.|SetInlineStatusVisible|PlaySavedFeedback|TabSelectionPlateBehavior\.|AddFrameObserver|RemoveFrameObserver|VisualState|PopupAnimation)' Scriblucent -g '*.cs' -g '*.xaml'
    $attachments | Set-Content -LiteralPath (Join-Path $Output 'attachments.txt') -Encoding utf8
    Write-Output "Inventoried $($calls.Count) animation and rendering lines; $($attachments.Count) attachment and lifecycle lines."
}
finally { Pop-Location }
