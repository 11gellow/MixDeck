using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AudioManager.Desktop.Views;

/// <summary>点击完整过渡，拖动按档位吸附；页面内容仍由原生 TabControl 管理。</summary>
public sealed class SlidingTabControl : TabControl
{
    private const double DetentHysteresis = 4;
    private static readonly Duration MotionDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly DependencyProperty SelectedLabelBrushProperty = DependencyProperty.Register(
        "SelectedLabelBrush", typeof(Brush), typeof(SlidingTabControl), new PropertyMetadata(null, OnPaletteChanged));
    private static readonly DependencyProperty OtherLabelBrushProperty = DependencyProperty.Register(
        "OtherLabelBrush", typeof(Brush), typeof(SlidingTabControl), new PropertyMetadata(null, OnPaletteChanged));
    private FrameworkElement? track;
    private FrameworkElement? indicator;
    private Window? owner;
    private readonly TranslateTransform translation = new();
    private readonly Dictionary<ContentPresenter, LabelMotion> labels = [];
    private AnimationClock? movement;
    private bool pressed;
    private bool dragging;
    private double pressedX;
    private double lastX;
    private int pressedIndex;

    // 可控时钟用于真实 WPF 夹具验证中间帧，避免依赖测试机的帧率和睡眠时间。
    internal bool? AnimationsEnabledOverride { get; set; }
    internal void SeekAnimations(TimeSpan elapsed)
    {
        movement?.Controller?.SeekAlignedToLastTick(elapsed, TimeSeekOrigin.BeginTime);
        foreach (var label in labels.Values) label.Clock?.Controller?.SeekAlignedToLastTick(elapsed, TimeSeekOrigin.BeginTime);
    }

    public SlidingTabControl()
    {
        SetResourceReference(SelectedLabelBrushProperty, "OnAccentBrush");
        SetResourceReference(OtherLabelBrushProperty, "SecondaryTextBrush");
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelInteraction(); };
        Loaded += (_, _) => { AttachOwner(); PositionIndicator(false); };
        Unloaded += (_, _) => { DetachOwner(); CancelInteraction(); };
    }

    public override void OnApplyTemplate()
    {
        CancelInteraction();
        if (track is not null)
        {
            track.PreviewMouseLeftButtonDown -= OnPress;
            track.PreviewMouseMove -= OnMove;
            track.PreviewMouseLeftButtonUp -= OnRelease;
            track.LostMouseCapture -= OnCaptureLost;
            track.SizeChanged -= OnTrackSizeChanged;
        }
        foreach (var label in labels.Values) label.Clock?.Controller?.Remove();
        labels.Clear();
        base.OnApplyTemplate();
        track = GetTemplateChild("PART_NavigationTrack") as FrameworkElement;
        indicator = GetTemplateChild("PART_NavigationIndicator") as FrameworkElement;
        if (indicator is not null) indicator.RenderTransform = translation;
        if (track is null) return;
        track.PreviewMouseLeftButtonDown += OnPress;
        track.PreviewMouseMove += OnMove;
        track.PreviewMouseLeftButtonUp += OnRelease;
        track.LostMouseCapture += OnCaptureLost;
        track.SizeChanged += OnTrackSizeChanged;
        PositionIndicator(false);
        if (IsLoaded) AttachOwner();
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (ReferenceEquals(e.Source, this)) PositionIndicator(true);
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    { if (track is not null) e.Handled = BeginNavigationDrag(e.GetPosition(track).X); }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!pressed || track is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelInteraction(); return; }
        MoveNavigationDrag(e.GetPosition(track).X);
        e.Handled = true;
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (!pressed || track is null) return;
        var point = e.GetPosition(track);
        EndNavigationDrag(point.X, point.Y >= 0 && point.Y <= track.ActualHeight);
        e.Handled = true;
    }

    internal bool BeginNavigationDrag(double x)
    {
        if (!IsEnabled || Items.Count == 0 || track is null || track.ActualWidth <= 0 || !double.IsFinite(x)) return false;
        if (pressed) CancelInteraction();
        Focus();
        pressedX = lastX = x;
        pressedIndex = IndexAt(x);
        dragging = false;
        pressed = track.CaptureMouse();
        // 按下仅记录状态；此处不能先跳动滑块，否则点击动画会丢失起点。
        return pressed;
    }

    internal void MoveNavigationDrag(double x)
    {
        if (!pressed || !IsEnabled || track is null || !double.IsFinite(x)) return;
        lastX = x;
        if (!dragging && Math.Abs(x - pressedX) < SystemParameters.MinimumHorizontalDragDistance) return;
        dragging = true;
        var width = track.ActualWidth / Items.Count;
        if (width <= 0) return;
        var index = Math.Clamp(SelectedIndex, 0, Items.Count - 1);
        while (index < Items.Count - 1 && x >= (index + 1) * width + DetentHysteresis) index++;
        while (index > 0 && x < index * width - DetentHysteresis) index--;
        SelectIndex(index);
    }

    internal void EndNavigationDrag(double? x = null, bool inside = true)
    {
        if (!pressed || track is null) return;
        var releaseX = x ?? lastX;
        if (x.HasValue) MoveNavigationDrag(releaseX);
        var click = !dragging && inside && releaseX >= 0 && releaseX <= track.ActualWidth && IndexAt(releaseX) == pressedIndex;
        pressed = dragging = false;
        if (track.IsMouseCaptured) track.ReleaseMouseCapture();
        if (click) SelectIndex(pressedIndex);
        // 拖动跨档时动画已启动，松手不能再次重启同一动画。
    }

    private int IndexAt(double x) => track is null || Items.Count == 0 || track.ActualWidth <= 0 ? -1
        : Math.Clamp((int)(Math.Clamp(x, 0, track.ActualWidth) / (track.ActualWidth / Items.Count)), 0, Items.Count - 1);

    private void SelectIndex(int index)
    {
        if (index < 0 || index >= Items.Count || index == SelectedIndex
            || ItemContainerGenerator.ContainerFromIndex(index) is TabItem { IsEnabled: false }) return;
        SetCurrentValue(SelectedIndexProperty, index);
    }

    private void OnCaptureLost(object sender, MouseEventArgs e)
    { if (pressed && track?.IsMouseCaptured != true) CancelInteraction(); }
    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => PositionIndicator(false);
    private void OnOwnerDeactivated(object? sender, EventArgs e) => CancelInteraction();
    private void AttachOwner()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, owner)) return;
        DetachOwner(); owner = window;
        if (owner is not null) owner.Deactivated += OnOwnerDeactivated;
    }
    private void DetachOwner()
    { if (owner is not null) owner.Deactivated -= OnOwnerDeactivated; owner = null; }

    private void CancelInteraction()
    {
        pressed = dragging = false;
        if (track?.IsMouseCaptured == true) track.ReleaseMouseCapture();
        PositionIndicator(false);
    }

    private bool CanAnimate => IsLoaded && IsEnabled && (AnimationsEnabledOverride ?? SystemParameters.ClientAreaAnimation);
    private void PositionIndicator(bool animate)
    {
        if (track is null || indicator is null || Items.Count == 0) return;
        var width = track.ActualWidth / Items.Count;
        indicator.Width = width;
        var target = Math.Clamp(SelectedIndex, 0, Items.Count - 1) * width;
        var current = translation.X;
        movement?.Controller?.Remove();
        translation.ApplyAnimationClock(TranslateTransform.XProperty, null);
        translation.X = target;
        movement = null;
        animate &= CanAnimate;
        UpdateLabels(animate);
        if (!animate || Math.Abs(current - target) < .01) return;
        translation.X = current;
        var clock = (AnimationClock)new DoubleAnimation(current, target, MotionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd
        }.CreateClock(true);
        movement = clock;
        clock.Completed += (_, _) =>
        {
            if (!ReferenceEquals(movement, clock)) return;
            translation.X = target;
            translation.ApplyAnimationClock(TranslateTransform.XProperty, null);
            clock.Controller?.Remove();
            movement = null;
        };
        translation.ApplyAnimationClock(TranslateTransform.XProperty, clock, HandoffBehavior.SnapshotAndReplace);
        clock.Controller!.Begin();
    }

    private void UpdateLabels(bool animate)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(index) is not TabItem tab) continue;
            tab.ApplyTemplate();
            if (tab.Template?.FindName("NavigationLabel", tab) is not ContentPresenter presenter) continue;
            var resource = GetValue(index == SelectedIndex ? SelectedLabelBrushProperty : OtherLabelBrushProperty);
            if (resource is not SolidColorBrush destination) continue;
            if (!labels.TryGetValue(presenter, out var state))
            {
                var initial = (TextElement.GetForeground(presenter) as SolidColorBrush)?.Color ?? destination.Color;
                state = new LabelMotion(new SolidColorBrush(initial));
                labels.Add(presenter, state);
                TextElement.SetForeground(presenter, state.Brush);
            }
            var from = state.Brush.Color;
            state.Clock?.Controller?.Remove();
            state.Brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
            state.Brush.Color = destination.Color;
            state.Clock = null;
            if (!animate || from == destination.Color) continue;
            state.Brush.Color = from;
            var clock = (AnimationClock)new ColorAnimation(from, destination.Color, MotionDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd }.CreateClock(true);
            state.Clock = clock;
            var targetColor = destination.Color;
            clock.Completed += (_, _) =>
            {
                if (!ReferenceEquals(state.Clock, clock)) return;
                state.Brush.Color = targetColor;
                state.Brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, null);
                clock.Controller?.Remove();
                state.Clock = null;
            };
            state.Brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock, HandoffBehavior.SnapshotAndReplace);
            clock.Controller!.Begin();
        }
    }
    private static void OnPaletteChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((SlidingTabControl)sender).UpdateLabels(false);
    private sealed class LabelMotion(SolidColorBrush brush)
    {
        public SolidColorBrush Brush { get; } = brush;
        public AnimationClock? Clock { get; set; }
    }
}
