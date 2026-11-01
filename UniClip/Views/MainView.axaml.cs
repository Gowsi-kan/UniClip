
using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using UniClip.ViewModels;

namespace UniClip.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.AttachClipboard(TopLevel.GetTopLevel(this)?.Clipboard);
        }
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.DetachClipboard();
        }
    }

    private async void PreviewClipboard_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var viewModel = DataContext as MainViewModel;
        if (viewModel is null)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            viewModel.ShowClipboardPreview(null);
            return;
        }

        try
        {
            viewModel.AttachClipboard(clipboard);
            viewModel.ShowClipboardPreview(await clipboard.TryGetTextAsync());
        }
        catch (Exception exception)
        {
            viewModel.ShowClipboardPreview($"Could not read the local clipboard: {exception.Message}");
        }
    }

    private async void ChooseFiles_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var viewModel = DataContext as MainViewModel;
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (viewModel is null || storageProvider is null)
        {
            return;
        }

        try
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = "Choose files to share"
            });

            viewModel.AddSelectedFiles(files);
        }
        catch (Exception exception)
        {
            viewModel.FilePickerStatus = $"Could not select files: {exception.Message}";
        }
    }

    private async void CopyPairingInvite_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || string.IsNullOrEmpty(viewModel.PairingInviteCode))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            viewModel.PairingStatus = "Clipboard access is unavailable on this device.";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(viewModel.PairingInviteCode);
            viewModel.PairingStatus = "Invite copied · expires in 10 minutes";
        }
        catch (Exception exception)
        {
            viewModel.PairingStatus = $"Could not copy invite: {exception.Message}";
        }
    }
}