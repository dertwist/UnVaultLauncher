using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnVault.App.Services;

namespace UnVault.App.ViewModels;

/// <summary>
/// An item's pictures at full size, over the window: the one its preview shows large, and with the arrows the ones before
/// and after. The view zooms and moves it; here it's only loaded, at more pixels than the preview needs.
/// </summary>
public partial class PictureViewerViewModel : ViewModelBase
{
    /// <summary>Pixels across asked of the store; its own picture when that's smaller. Enough to zoom into on a 4K screen.</summary>
    private const int FullWidth = 2560;

    private readonly MainViewModel _owner;
    private ThumbnailCache.Use? _use;

    public PictureViewerViewModel(MainViewModel owner, FabItemViewModel item)
    {
        _owner = owner;
        Item = item;
        item.PropertyChanged += OnItemChanged;
        Load();
    }

    /// <summary>The item whose gallery this goes through; its arrows and position are shown here too.</summary>
    public FabItemViewModel Item { get; }

    /// <summary>The current picture at full size; the preview's smaller copy until that's loaded.</summary>
    [ObservableProperty] public partial Bitmap? Picture { get; private set; }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FabItemViewModel.CurrentShot))
            Load();
    }

    private async void Load()
    {
        var thumbnails = _owner.Services.Thumbnails;
        Release();
        if (Item.CurrentShot is not { } shot)
        {
            Close(); // the preview closed, or the item is gone after a reload
            return;
        }

        Picture = Item.Picture;
        var use = _use = thumbnails.Get(shot.URL, shot.Key, shot.Uploaded, atWidth: FullWidth);
        try
        {
            if (await use.Picture is { } bitmap && _use == use) // not moved on meanwhile
                Picture = bitmap;
        }
        catch (Exception)
        {
            // async void: an escaping exception would end the app. The smaller copy stays.
        }
    }

    private void Release()
    {
        if (_use is not { } use)
            return;
        _use = null;
        Picture = null; // first, as the cache may free the picture once it's back
        _owner.Services.Thumbnails.Release(use);
    }

    /// <summary>The viewer is no longer the dialog, however it was closed: its picture goes back to the cache.</summary>
    internal void Closed()
    {
        Item.PropertyChanged -= OnItemChanged;
        Release();
    }

    [RelayCommand]
    private void Close() => _owner.Close(this);
}
