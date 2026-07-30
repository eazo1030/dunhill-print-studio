using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dunhill.PrintStudio.Models;
using Dunhill.PrintStudio.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Dunhill.PrintStudio.Views;

public partial class DesignerView : UserControl
{
    public DesignerView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DesignerViewModel>();
    }

    // ---- Per-element drag handlers (registered via EventSetter on each
    //      ContentPresenter in DesignerView.xaml). Attached directly to the
    //      presenter so WPF's built-in DragDrop on the inner element never
    //      gets a chance to fire — that was the source of the red '+'
    //      no-drop cursor and the "elements don't move" symptom.

    private LabelElement? _dragElement;
    private double _dragStartMouseX, _dragStartMouseY;
    private int _dragStartElementX, _dragStartElementY;
    private bool _dragMoved;
    private ContentPresenter? _dragHost;
    private DesignerViewModel? Vm => DataContext as DesignerViewModel;

    private void OnElementMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ContentPresenter presenter) return;
        if (presenter.DataContext is not LabelElement element) return;

        _dragElement     = element;
        _dragHost        = presenter;
        _dragStartMouseX  = e.GetPosition(presenter).X;
        _dragStartMouseY  = e.GetPosition(presenter).Y;
        _dragStartElementX = element.X;
        _dragStartElementY = element.Y;
        _dragMoved        = false;

        // Push ONE undo snapshot for the whole drag — every OnElementMouseMove
        // just updates X/Y on the same element.
        DesignerViewModelBridge.PushSnapshot();

        presenter.CaptureMouse();
        e.Handled = true;
    }

    private void OnElementMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragElement == null || _dragHost == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(_dragHost);
        var dx = pos.X - _dragStartMouseX;
        var dy = pos.Y - _dragStartMouseY;

        if (!_dragMoved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
        _dragMoved = true;

        // Clamp to the label canvas bounds and snap to other elements / 8-dot grid.
        var canvas = FindLabelCanvas(_dragHost);
        var width  = canvas != null ? (int)canvas.ActualWidth  : int.MaxValue;
        var height = canvas != null ? (int)canvas.ActualHeight : int.MaxValue;

        var rawX = _dragStartElementX + (int)Math.Round(dx);
        var rawY = _dragStartElementY + (int)Math.Round(dy);
        var snappedX = ApplySnap(rawX, width,  isVertical: true);
        var snappedY = ApplySnap(rawY, height, isVertical: false);

        _dragElement.X = Math.Max(0, Math.Min(width,  snappedX));
        _dragElement.Y = Math.Max(0, Math.Min(height, snappedY));
    }

    private const int GridSize       = 8;
    private const int SnapThreshold  = 6;

    /// <summary>
    /// Snap to: label edges, every 8-dot grid line, and other elements'
    /// left/right/top/bottom/center (within a 6-dot threshold).
    /// </summary>
    private int ApplySnap(int raw, int limit, bool isVertical)
    {
        var targets = new List<(int pos, int weight)>
        {
            (0, 1),
            (limit, 1),
        };
        // Grid lines
        for (int g = 0; g <= limit; g += GridSize) targets.Add((g, 1));

        if (Vm != null)
        {
            foreach (var other in Vm.Elements)
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
        }

        int best = raw, bestDelta = SnapThreshold + 1;
        foreach (var (pos, _) in targets)
        {
            var d = Math.Abs(pos - raw);
            if (d <= SnapThreshold && d < bestDelta) { bestDelta = d; best = pos; }
        }
        return best;
    }

    private static int ElementWidth(LabelElement el) => el.Type switch
    {
        "text"    => el.FontWidth * Math.Max(1, el.Content?.Length ?? 0),
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

    private void OnElementMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragElement == null || _dragHost == null) return;
        _dragHost.ReleaseMouseCapture();
        _dragElement = null;
        _dragHost = null;
        _dragMoved = false;
        // Notify the VM so CanUndo refreshes (the snapshot was pushed on mousedown)
        DesignerViewModelBridge.OnDragCompleted?.Invoke();
    }

    private static FrameworkElement? FindLabelCanvas(DependencyObject start)
    {
        DependencyObject? cur = start;
        while (cur != null)
        {
            if (cur is FrameworkElement fe && fe.Name == "LabelCanvas") return fe;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
    }
}

/// <summary>
/// Shared accessor the per-element drag handlers (in DesignerView.xaml.cs)
/// use to talk to the DesignerViewModel: push an undo snapshot at drag-start,
/// refresh CanUndo on drag-end, and read the currently-selected element for
/// keyboard nudging. DesignerViewModel assigns the delegates in its ctor.
/// </summary>
public static class DesignerViewModelBridge
{
    /// <summary>Currently selected element (for keyboard nudging).</summary>
    public static LabelElement? SelectedElement { get; set; }

    /// <summary>Action the VM assigns to push an undo snapshot (called on drag-start).</summary>
    public static Action? PushSnapshot { get; set; }

    /// <summary>Action the VM assigns to fire after a drag finishes (refreshes CanUndo).</summary>
    public static Action? OnDragCompleted { get; set; }
}