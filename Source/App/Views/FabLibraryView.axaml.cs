using Avalonia.Controls;
using Avalonia.Threading;
using UnVault.App.Services;
using UnVault.App.ViewModels;

namespace UnVault.App.Views;

public partial class FabLibraryView : UserControl
{
    /// <summary>Screens' worth of scrolling after which the memory it took is worth handing back.</summary>
    private const double ScreensBeforeRelief = 5;

    private FabLibraryViewModel? _viewModel;
    private readonly DispatcherTimer _scrollSettled = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private double _scrolledSinceRelief;

    public FabLibraryView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, _) => UpdateColumns();

        // A long scroll loads and drops many pictures. Once it stops, the memory that took goes back to Windows; not
        // during it, nor after a short one, as handing it back briefly pauses the app.
        Scroller.ScrollChanged += (_, e) =>
        {
            _scrolledSinceRelief += Math.Abs(e.OffsetDelta.Y);
            _scrollSettled.Stop();
            _scrollSettled.Start();
        };
        _scrollSettled.Tick += (_, _) =>
        {
            _scrollSettled.Stop();
            if (_scrolledSinceRelief < ScreensBeforeRelief * Scroller.Viewport.Height)
                return;
            _scrolledSinceRelief = 0;
            MemoryRelief.Release();
        };

        // The preview's left edge is dragged to make it wider or narrower; the grid keeps room for a column of tiles.
        PreviewEdge.PointerPressed += (_, e) =>
        {
            if (_viewModel is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            _resizeFrom = (e.GetPosition(this).X, _viewModel.PreviewWidth);
            e.Pointer.Capture(PreviewEdge);
            e.Handled = true;
        };
        PreviewEdge.PointerMoved += (_, e) =>
        {
            if (_viewModel is null || _resizeFrom is not var (pointerX, width))
                return;
            double room = Bounds.Width - FabRowView.TileWidth - 60;
            _viewModel.PreviewWidth = Math.Min(width + pointerX - e.GetPosition(this).X, Math.Max(FabLibraryViewModel.MinPreviewWidth, room));
        };
        PreviewEdge.PointerReleased += (_, e) =>
        {
            if (_resizeFrom is null)
                return;
            _resizeFrom = null;
            e.Pointer.Capture(null);
            _viewModel?.SavePreviewWidth();
        };
        PreviewEdge.PointerCaptureLost += (_, _) => _resizeFrom = null;
    }

    /// <summary>While the preview's edge is dragged: where the pointer was when it began, and how wide the preview was.</summary>
    private (double PointerX, double Width)? _resizeFrom;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.FilterChanged -= OnFilterChanged;
        _viewModel = DataContext as FabLibraryViewModel;
        if (_viewModel is not null)
            _viewModel.FilterChanged += OnFilterChanged;
        UpdateColumns();
    }

    /// <summary>As many columns as fit at the minimum tile width; the rows then stretch the tiles to fill the page.</summary>
    private void UpdateColumns()
    {
        if (_viewModel is not null && Page.Bounds.Width > 0)
            _viewModel.Columns = FabRowView.ColumnsFor(Page.Bounds.Width);
    }

    // New results start at the top, rather than wherever the previous list was scrolled to.
    private void OnFilterChanged(object? sender, EventArgs e) => Scroller.Offset = default;
}
