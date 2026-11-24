using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using UnVault.App.ViewModels;

namespace UnVault.App.Views;

public partial class FabPreviewView : UserControl
{
    private FabItemViewModel? _item;

    public FabPreviewView()
    {
        InitializeComponent();
        // The large picture keeps its shape while the user drags the preview wider or narrower.
        SizeChanged += (_, e) => PictureHost.Height = Math.Round(e.NewSize.Width * 9 / 16);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_item is not null)
            _item.PropertyChanged -= OnItemChanged;
        _item = DataContext as FabItemViewModel;
        if (_item is not null)
            _item.PropertyChanged += OnItemChanged;
    }

    /// <summary>The arrows can go to a picture the strip has scrolled away: it's brought back into sight.</summary>
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FabItemViewModel.CurrentShot) && _item?.CurrentShot is { } shot)
            Dispatcher.UIThread.Post(() => ShotList.ContainerFromItem(shot)?.BringIntoView(), DispatcherPriority.Background);
    }
}
