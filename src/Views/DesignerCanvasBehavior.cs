using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.ViewModels;

namespace Dunhill.PrintStudio.Views;

/// <summary>
/// Mouse-drag-to-move + grid/edge-snap + snap-guide overlay for the
/// Label Designer preview canvas. Attached to the preview Grid by
/// DesignerView.xaml. Drag math lives here; undo/redo snapshots live
/// on DesignerViewModel (this helper just fires events when a drag
/// starts/ends so the VM can push a snapshot at the right moment).
/// </summary>
public sealed class DesignerCanvasBehavior
{
    public const int GridSize = 8;                  // snap to multiples of 8 dots
    public const int SnapThreshold = 6;             // pixels to consider a target "near"

    // ---- Attached event: drag completed (started → ended) so the VM can
    //      push ONE undo snapshot for the whole drag instead of one per
    //      MouseMove tick. Payload = the LabelElement that moved. ----
    public static readonly RoutedEvent DragCompletedEvent =
        EventManager.RegisterRoutedEvent(
            "DragCompleted",
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(DesignerCanvasBehavior));

    public static void AddDragCompletedHandler(UIElement d, RoutedEventHandler h)
        => d.AddHandler(DragCompletedEvent, h);
    public static void RemoveDragCompletedHandler(UIElement d, RoutedEventHandler h)
        => d.RemoveHandler(DragCompletedEvent, h);

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(DesignerCanvasBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject d, bool v) => d.SetValue(IsEnabledProperty, v);
    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host) return;
        if ((bool)e.NewValue)
        {
            host.PreviewMouseLeftButtonDown += OnMouseDown;
            host.PreviewMouseMove            += OnMouseMove;
            host.PreviewMouseLeftButtonUp    += OnMouseUp;
            host.PreviewKeyDown              += OnKeyDown;
            _host = host;
        }
        else
        {
            host.PreviewMouseLeftButtonDown -= OnMouseDown;
            host.PreviewMouseMove            -= OnMouseMove;
            host.PreviewMouseLeftButtonUp    -= OnMouseUp;
            host.PreviewKeyDown              -= OnKeyDown;
            _host = null;
        }
    }

    // ---- Drag state ----
    private static LabelElement? _dragElement;
    private static Point _dragStartCanvasPos;        // mouse pos when drag started
    private static int _dragStartElementX, _dragStartElementY;
    private static bool _dragMoved;
    private static FrameworkElement? _host;
    private static Canvas? _guideCanvas;

    // Snap-guide visuals (one vertical + one horizontal line)
    private static System.Windows.Shapes.Line? _vGuide, _hGuide;

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var host = (FrameworkElement)sender;
        // Hit-test from the mouse pos to find a ContentPresenter wrapping a LabelElement
        var hit = e.OriginalSource as DependencyObject;
        var element = FindLabelElement(hit);
        if (element == null) return;

        // Push ONE undo snapshot at drag start — every OnMouseMove below just
        // updates X/Y on this same element without re-pushing. On MouseUp we
        // notify the VM so it knows the drag finished and updates CanUndo.
        DesignerViewModelBridge.PushSnapshot?.Invoke();

        _dragElement = element;
        _dragStartCanvasPos = e.GetPosition(host);
        _dragStartElementX = element.X;
        _dragStartElementY = element.Y;
        _dragMoved = false;

        EnsureGuides(host);
        HideGuides();

        // Capture so MouseMove keeps firing even if cursor leaves the element
        host.CaptureMouse();
        e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragElement == null || _host == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var host = _host;
        var cur = e.GetPosition(host);
        var dx = cur.X - _dragStartCanvasPos.X;
        var dy = cur.Y - _dragStartCanvasPos.Y;

        // Only start "moving" after 2px so clicks don't jitter
        if (!_dragMoved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
        _dragMoved = true;

        var rawX = _dragStartElementX + (int)Math.Round(dx);
        var rawY = _dragStartElementY + (int)Math.Round(dy);

        // Compute snap targets along each axis
        var snappedX = ApplySnap(host, rawX, isVertical: true);
        var snappedY = ApplySnap(host, rawY, isVertical: false);

        _dragElement.X = Math.Max(0, snappedX);
        _dragElement.Y = Math.Max(0, snappedY);

        // Update guide lines
        UpdateVerticalGuide(host, snappedX);
        UpdateHorizontalGuide(host, snappedY);
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragElement == null || _host == null) return;
        _host.ReleaseMouseCapture();
        var moved = _dragMoved;
        var element = _dragElement;

        HideGuides();
        _dragElement = null;
        _dragMoved = false;

        if (moved && element != null)
        {
            // Fire DragCompleted on the canvas so the VM can push one
            // undo snapshot for the whole drag (not 60 per second).
            DesignerViewModelBridge.OnDragCompleted?.Invoke();
            var args = new RoutedEventArgs(DragCompletedEvent, element);
            ((FrameworkElement)sender).RaiseEvent(args);
        }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Nudge selected element by 1 dot on arrow keys (with shift = 8 = one grid)
        if (DesignerViewModelBridge.SelectedElement == null) return;
        var el = DesignerViewModelBridge.SelectedElement;
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? GridSize : 1;
        bool handled = false;
        switch (e.Key)
        {
            case Key.Left:  el.X -= step; handled = true; break;
            case Key.Right: el.X += step; handled = true; break;
            case Key.Up:    el.Y -= step; handled = true; break;
            case Key.Down:  el.Y += step; handled = true; break;
        }
        if (handled) e.Handled = true;
    }

    // ---- Snap math ----
    private static int ApplySnap(FrameworkElement host, int raw, bool isVertical)
    {
        // Collect all candidate snap positions on this axis
        var targets = new List<(int pos, int weight)>();

        // Label edges (always)
        targets.Add((0, 1));
        var w = (int)host.Width;
        var h = (int)host.Height;
        if (isVertical) targets.Add((w, 1));
        else            targets.Add((h, 1));

        // Grid lines (every 8 dots)
        var limit = isVertical ? w : h;
        for (int g = 0; g <= limit; g += GridSize) targets.Add((g, 1));

        // Other elements (excluding the one being dragged)
        var tplElements = GetHostElements(host);
        foreach (var other in tplElements)
        {
            if (other == _dragElement) continue;
            if (isVertical)
            {
                targets.Add((other.X, 2));
                targets.Add((other.X + ElementWidth(other), 2));
                targets.Add((other.X + ElementWidth(other) / 2, 1));
            }
            else
            {
                targets.Add((other.Y, 2));
                targets.Add((other.Y + ElementHeight(other), 2));
                targets.Add((other.Y + ElementHeight(other) / 2, 1));
            }
        }

        // Find nearest target within SnapThreshold
        int best = raw, bestDelta = SnapThreshold + 1;
        foreach (var (pos, _) in targets)
        {
            var d = Math.Abs(pos - raw);
            if (d <= SnapThreshold && d < bestDelta) { bestDelta = d; best = pos; }
        }
        return best;
    }

    // Width/height helpers — try the bound element first, then the framework element
    private static int ElementWidth(LabelElement el) => el.Type switch
    {
        "text"    => el.FontWidth * Math.Max(1, (el.Content?.Length ?? 0)),
        "barcode" => (int)(el.BarcodeHeight * 1.6),
        "qrcode"  => el.QrMagnification * 30,
        "box"     => el.Width,
        "line"    => el.Width,
        "rfid"    => 40,
        _         => 0,
    };
    private static int ElementHeight(LabelElement el) => el.Type switch
    {
        "text"    => el.FontHeight,
        "barcode" => el.BarcodeHeight,
        "qrcode"  => el.QrMagnification * 30,
        "box"     => el.Height,
        "line"    => 2,
        "rfid"    => 24,
        _         => 0,
    };

    // Walks the visual tree from the hit target up to the ContentPresenter that
    // hosts the LabelElement DataContext.
    private static LabelElement? FindLabelElement(DependencyObject? hit)
    {
        while (hit != null)
        {
            if (hit is ContentPresenter cp && cp.DataContext is LabelElement le) return le;
            if (hit is FrameworkElement fe && fe.DataContext is LabelElement le2) return le2;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    // Reads the elements list from the DesignerViewModel via the bridge.
    // The bridge is also where PushSnapshot/OnDragCompleted callbacks live.
    // Cleaner alternative would be to expose Elements directly on the bridge,
    // but for now we pull them from the DataContext of the host.
    private static IEnumerable<LabelElement> GetHostElements(FrameworkElement host)
    {
        if (host.DataContext is DesignerViewModel vm) return vm.Elements;
        return Array.Empty<LabelElement>();
    }

    // ---- Snap guide visuals ----
    private static void EnsureGuides(FrameworkElement host)
    {
        // Find the named GuideCanvas in the visual tree (added by DesignerView.xaml)
        _guideCanvas ??= FindChild<Canvas>(host, "GuideCanvas");

        if (_vGuide == null)
        {
            _vGuide = new System.Windows.Shapes.Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 0xEF, 0x44, 0x44)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
        }
        if (_hGuide == null)
        {
            _hGuide = new System.Windows.Shapes.Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 0xEF, 0x44, 0x44)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
        }
        if (_guideCanvas != null)
        {
            if (!_guideCanvas.Children.Contains(_vGuide))
            {
                _guideCanvas.Children.Add(_vGuide);
                System.Windows.Controls.Panel.SetZIndex(_vGuide, 999);
            }
            if (!_guideCanvas.Children.Contains(_hGuide))
            {
                _guideCanvas.Children.Add(_hGuide);
                System.Windows.Controls.Panel.SetZIndex(_hGuide, 999);
            }
        }
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t && t.Name == name) return t;
            var nested = FindChild<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void UpdateVerticalGuide(FrameworkElement host, int x)
    {
        if (_vGuide == null) return;
        _vGuide.X1 = _vGuide.X2 = x;
        _vGuide.Y1 = 0;
        _vGuide.Y2 = host.Height;
        _vGuide.Visibility = Visibility.Visible;
    }
    private static void UpdateHorizontalGuide(FrameworkElement host, int y)
    {
        if (_hGuide == null) return;
        _hGuide.Y1 = _hGuide.Y2 = y;
        _hGuide.X1 = 0;
        _hGuide.X2 = host.Width;
        _hGuide.Visibility = Visibility.Visible;
    }
    private static void HideGuides()
    {
        if (_vGuide != null) _vGuide.Visibility = Visibility.Collapsed;
        if (_hGuide != null) _hGuide.Visibility = Visibility.Collapsed;
    }
}

/// <summary>
/// Thin accessor the canvas helper uses to read the currently-selected
/// element for keyboard nudging. DesignerViewModel assigns the static
/// SelectedElement in its setter.
/// </summary>
public static class DesignerViewModelBridge
{
    /// <summary>Currently selected element (for keyboard nudging).</summary>
    public static LabelElement? SelectedElement { get; set; }

    /// <summary>Action the VM assigns to push an undo snapshot (called from canvas drag-start).</summary>
    public static Action? PushSnapshot { get; set; }

    /// <summary>Action the VM assigns to fire after a drag finishes (refreshes CanUndo).</summary>
    public static Action? OnDragCompleted { get; set; }
}