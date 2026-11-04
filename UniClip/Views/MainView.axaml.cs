
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
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

    private void PinDigit_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox ||
            DataContext is not MainViewModel viewModel ||
            !int.TryParse(textBox.Tag?.ToString(), out var digitNumber))
        {
            return;
        }

        var digits = new string((textBox.Text ?? string.Empty)
            .Where(character => character is >= '0' and <= '9')
            .ToArray());
        if (digits.Length > 1)
        {
            viewModel.SetPairingPin(digits);
            FocusPinDigit(Math.Min(digits.Length, 5));
            return;
        }

        var digit = digits.Length == 0 ? string.Empty : digits;
        if (textBox.Text != digit)
        {
            textBox.Text = digit;
            textBox.CaretIndex = digit.Length;
        }

        viewModel.SetPinDigit(digitNumber - 1, digit);
        if (digit.Length == 1 && digitNumber < 6)
        {
            FocusPinDigit(digitNumber);
        }
    }

    private async void PinDigit_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox ||
            !int.TryParse(textBox.Tag?.ToString(), out var digitNumber))
        {
            return;
        }

        var index = digitNumber - 1;
        if (e.Key == Key.V &&
            (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            e.Handled = true;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            var clipboardText = await clipboard.TryGetTextAsync();
            if (DataContext is MainViewModel viewModel && clipboardText is not null)
            {
                var digits = new string([.. clipboardText
                    .Where(character => character is >= '0' and <= '9')
                    .Take(6)]);
                viewModel.SetPairingPin(digits);
                FocusPinDigit(Math.Min(digits.Length, 5));
            }

            return;
        }

        if (e.Key == Key.Back && string.IsNullOrEmpty(textBox.Text) && index > 0)
        {
            e.Handled = true;
            FocusPinDigit(index - 1);
        }
        else if (e.Key == Key.Left && index > 0)
        {
            e.Handled = true;
            FocusPinDigit(index - 1);
        }
        else if (e.Key == Key.Right && index < 5)
        {
            e.Handled = true;
            FocusPinDigit(index + 1);
        }
    }

    private void FocusPinDigit(int index)
    {
        var textBox = this.FindControl<TextBox>($"PinDigit{index + 1}");
        textBox?.Focus();
        textBox?.SelectAll();
    }
}