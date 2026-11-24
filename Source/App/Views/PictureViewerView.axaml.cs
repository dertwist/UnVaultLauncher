using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using UnVault.App.ViewModels;

namespace UnVault.App.Views;

/// <summary>
/// Shows a picture nearly as large as the window. The wheel zooms towards the pointer, a drag moves the zoomed picture,
/// a double click fits it again; the left and right arrow keys go through the gallery.
/// </summary>
public partial class PictureViewerView : UserControl
{
    /// <summary>The window's edge left clear around the viewer.</summary>
    private const double Edge = 40;

    private const double MaxZoom = 12;

    private TopLevel? _window;
    private PictureViewerViewModel? _viewModel;
    private double _zoom = 1;
    private Vector _offset;
    private Point? _dragFrom;

    public PictureViewerView()
    {
        InitializeComponent();
        Stage.PointerWheelChanged += OnWheel;
        Stage.PointerPressed += OnPressed;
        Stage.PointerMoved += OnMoved;
        Stage.PointerReleased += (_, e) =>
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
        };
        Stage.DoubleTapped += (_, _) => Fit();
        Stage.SizeChanged += (_, _) => Apply();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this);
        if (_window is null)
            return;
        _window.SizeChanged += OnWindowSizeChanged;
        // Before the dialog's own handling, which has the focus: the arrows mean the pictures here.
        _window.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        FillWindow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null)
        {
            _window.SizeChanged -= OnWindowSizeChanged;
            _window.RemoveHandler(KeyDownEvent, OnKeyDown);
            _window = null;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.Item.PropertyChanged -= OnItemChanged;
        _viewModel = DataContext as PictureViewerViewModel;
        if (_viewModel is not null)
            _viewModel.Item.PropertyChanged += OnItemChanged;
        Fit();
    }

    /// <summary>Another picture of the gallery starts fitted, not at the zoom the last one was left at.</summary>
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FabItemViewModel.CurrentShot))
            Fit();
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => FillWindow();

    private void FillWindow()
    {
        if (_window is null)
            return;
        Width = Math.Max(320, _window.ClientSize.Width - 2 * Edge);
        Height = Math.Max(240, _window.ClientSize.Height - 2 * Edge);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.Key switch
        {
            Key.Left => _viewModel?.Item.PreviousShotCommand,
            Key.Right => _viewModel?.Item.NextShotCommand,
            _ => null,
        };
        if (command is null)
            return;
        command.Execute(null);
        e.Handled = true;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        double zoom = Math.Clamp(_zoom * Math.Pow(1.2, e.Delta.Y), 1, MaxZoom);
        // What's under the pointer stays under it.
        var pointer = e.GetPosition(Stage);
        _offset = (Vector)pointer - ((Vector)pointer - _offset) * (zoom / _zoom);
        _zoom = zoom;
        Apply();
        e.Handled = true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_zoom <= 1 || !e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed)
            return;
        _dragFrom = e.GetPosition(Stage);
        e.Pointer.Capture(Stage);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom is not { } from)
            return;
        var now = e.GetPosition(Stage);
        _offset += now - from;
        _dragFrom = now;
        Apply();
    }

    private void Fit()
    {
        _zoom = 1;
        _offset = default;
        Apply();
    }

    /// <summary>Zoomed, the picture's place always covers the viewer: it can't be dragged out of sight.</summary>
    private void Apply()
    {
        var size = Stage.Bounds.Size;
        _offset = new Vector(Math.Clamp(_offset.X, size.Width * (1 - _zoom), 0), Math.Clamp(_offset.Y, size.Height * (1 - _zoom), 0));
        Picture.RenderTransform = new MatrixTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_offset.X, _offset.Y));
        Stage.Cursor = _zoom > 1 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
    }
}
