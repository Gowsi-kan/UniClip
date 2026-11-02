using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniClip.ViewModels;

namespace UniClip.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
        {
            WindowDecorations = WindowDecorations.None;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 32;
            RootLayout.RowDefinitions[0].Height = new GridLength(32);
            TitleBar.IsVisible = true;
            ResizeHandles.IsVisible = true;
            Win32Properties.SetWindowCornerPreference(this, Win32Properties.WindowCornerPreference.Round);
            UpdateWindowStateVisuals();
        }

    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.InitializePairingAsync();
        }
    }

    private void MinimizeWindow_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = Avalonia.Controls.WindowState.Minimized;

    private void ToggleWindowState_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? Avalonia.Controls.WindowState.Normal
            : Avalonia.Controls.WindowState.Maximized;

    private void CloseWindow_OnClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && MaximizeGlyph is not null)
        {
            UpdateWindowStateVisuals();
        }
    }

    private void UpdateWindowStateVisuals()
    {
        var isMaximized = WindowState == Avalonia.Controls.WindowState.Maximized;
        MaximizeGlyph.IsVisible = !isMaximized;
        RestoreGlyph.IsVisible = isMaximized;
        ResizeHandles.IsVisible = WindowState == Avalonia.Controls.WindowState.Normal;
    }
}
