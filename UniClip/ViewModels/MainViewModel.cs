using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using UniClip.Services;

namespace UniClip.ViewModels;

public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly PairingCoordinator _pairingCoordinator = new();
    private readonly SemaphoreSlim _incomingApprovalLock = new(1, 1);
    private TaskCompletionSource<bool>? _pendingIncomingApproval;
    private readonly DispatcherTimer _clipboardTimer = new();
    private IClipboard? _clipboard;
    private string? _lastObservedClipboardText;
    private bool _captureClipboardBaseline;
    private int _clipboardPollInProgress;
    private string? _lastClipboardReadError;

    public MainViewModel()
    {
        _pairingCoordinator.PeerConnected += OnPeerConnected;
        _pairingCoordinator.TransferProgressChanged += OnTransferProgressChanged;
        _pairingCoordinator.FileReceived += OnFileReceived;
        _pairingCoordinator.IncomingTransferApprovalRequested += ApproveIncomingTransferAsync;
        _pairingCoordinator.IncomingClipboardRequested += ApplyIncomingClipboardAsync;
        _clipboardTimer.Interval = TimeSpan.FromMilliseconds(800);
        _clipboardTimer.Tick += OnClipboardTimerTick;
        CanPair = !OperatingSystem.IsBrowser();
        if (!CanPair)
        {
            PairingStatus = "Pairing is unavailable in the browser preview.";
        }
    }

    [ObservableProperty]
    public partial string ClipboardPreview { get; set; } = "Your clipboard stays on this device until you pair another one.";

    [ObservableProperty]
    public partial bool IsClipboardSyncEnabled { get; set; }

    [ObservableProperty]
    public partial bool CanClipboardSync { get; set; }

    [ObservableProperty]
    public partial string ClipboardSyncStatus { get; set; } = "Pair a device, then opt in on both devices.";

    [ObservableProperty]
    public partial string FilePickerStatus { get; set; } = "Nothing selected";

    [ObservableProperty]
    public partial string PairingStatus { get; set; } = "Start a temporary, encrypted device pairing.";

    [ObservableProperty]
    public partial string PairingInviteCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InvitationInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "Not connected";

    [ObservableProperty]
    public partial Bitmap? PairingQrCode { get; set; }

    [ObservableProperty]
    public partial bool IsPairingBusy { get; set; }

    [ObservableProperty]
    public partial bool CanPair { get; set; }

    [ObservableProperty]
    public partial bool HasPairingInvite { get; set; }

    [ObservableProperty]
    public partial int PairedDeviceCount { get; set; }

    [ObservableProperty]
    public partial bool CanSendFiles { get; set; }

    [ObservableProperty]
    public partial string TransferStatus { get; set; } = "Choose files, then send them to a paired device.";

    [ObservableProperty]
    public partial string IncomingTransferPrompt { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasIncomingTransferRequest { get; set; }

    public ObservableCollection<SelectedFile> SelectedFiles { get; } = [];

    public async ValueTask DisposeAsync()
    {
        _clipboardTimer.Stop();
        _pendingIncomingApproval?.TrySetResult(false);
        PairingQrCode?.Dispose();
        await _pairingCoordinator.DisposeAsync();
        _incomingApprovalLock.Dispose();
    }

    public void AttachClipboard(IClipboard? clipboard)
    {
        _clipboard = clipboard;
        _captureClipboardBaseline = true;
        UpdateClipboardSyncTimer();
    }

    public void DetachClipboard()
    {
        _clipboardTimer.Stop();
        _clipboard = null;
    }

    partial void OnIsClipboardSyncEnabledChanged(bool value)
    {
        _captureClipboardBaseline = true;
        ClipboardSyncStatus = value
            ? "Sync is opt-in. Turn it on on both paired devices."
            : "Clipboard sync is off.";
        UpdateClipboardSyncTimer();
    }

    public void ShowClipboardPreview(string? text)
    {
        ClipboardPreview = string.IsNullOrWhiteSpace(text)
            ? "There is no text in your clipboard right now."
            : text.Length > 180
                ? $"{text[..180]}…"
                : text;
    }

    public void AddSelectedFiles(IEnumerable<IStorageFile> files)
    {
        foreach (var file in files)
        {
            SelectedFiles.Add(new SelectedFile(file));
        }

        var count = SelectedFiles.Count;
        FilePickerStatus = count == 0
            ? "Nothing selected"
            : $"{count} file{(count == 1 ? string.Empty : "s")} ready to send after pairing";
        CanSendFiles = count > 0 && PairedDeviceCount > 0 && !IsPairingBusy;
    }

    [RelayCommand]
    private async Task CreatePairingInviteAsync()
    {
        IsPairingBusy = true;
        CanPair = false;
        PairingStatus = "Starting a secure listener…";
        try
        {
            var invite = await _pairingCoordinator.CreateInviteAsync(CancellationToken.None);
            PairingInviteCode = invite.Encode();

            using var qrData = QRCodeGenerator.GenerateQrCode(PairingInviteCode, QRCodeGenerator.ECCLevel.M);
            var imageBytes = new PngByteQRCode(qrData).GetGraphic(8);
            using var imageStream = new MemoryStream(imageBytes);
            var newImage = new Bitmap(imageStream);
            PairingQrCode?.Dispose();
            PairingQrCode = newImage;
            HasPairingInvite = true;

            PairingStatus = "Invite ready · expires in 10 minutes · authenticated by Noise";
        }
        catch (Exception exception)
        {
            PairingStatus = $"Could not create pairing invite: {exception.Message}";
        }
        finally
        {
            IsPairingBusy = false;
            CanPair = !OperatingSystem.IsBrowser();
        }
    }

    [RelayCommand]
    private async Task ConnectToInviteAsync()
    {
        IsPairingBusy = true;
        CanPair = false;
        ConnectionStatus = "Connecting and verifying device identity…";
        try
        {
            var peerId = await _pairingCoordinator.ConnectAsync(InvitationInput, CancellationToken.None);
            ConnectionStatus = $"Securely paired · {peerId}";
            PairedDeviceCount = 1;
            CanSendFiles = SelectedFiles.Count > 0;
            CanClipboardSync = !OperatingSystem.IsBrowser();
            ClipboardSyncStatus = IsClipboardSyncEnabled
                ? "Paired · checking for clipboard changes…"
                : "Paired · enable sync on both devices to share text.";
            UpdateClipboardSyncTimer();
        }
        catch (Exception exception)
        {
            ConnectionStatus = $"Could not connect: {exception.Message}";
        }
        finally
        {
            IsPairingBusy = false;
            CanPair = !OperatingSystem.IsBrowser();
        }
    }

    [RelayCommand]
    private async Task SendSelectedFilesAsync()
    {
        if (PairedDeviceCount == 0 || SelectedFiles.Count == 0)
        {
            TransferStatus = "Pair with a device and choose at least one file first.";
            return;
        }

        CanSendFiles = false;
        IsPairingBusy = true;
        try
        {
            foreach (var selectedFile in SelectedFiles.ToArray())
            {
                var properties = await selectedFile.File.GetBasicPropertiesAsync();
                if (properties.Size is not ulong size || size > long.MaxValue)
                {
                    throw new IOException($"Could not determine the size of {selectedFile.Name}.");
                }

                await using var source = await selectedFile.File.OpenReadAsync();
                TransferStatus = $"Sending {selectedFile.Name}…";
                var result = await _pairingCoordinator.SendFileAsync(source, selectedFile.Name, (long)size);
                TransferStatus = result;
                SelectedFiles.Remove(selectedFile);
            }

            var count = SelectedFiles.Count;
            FilePickerStatus = count == 0
                ? "Nothing selected"
                : $"{count} file{(count == 1 ? string.Empty : "s")} ready to send";
        }
        catch (Exception exception)
        {
            TransferStatus = $"File transfer failed: {exception.Message}";
        }
        finally
        {
            IsPairingBusy = false;
            CanPair = !OperatingSystem.IsBrowser();
            CanSendFiles = PairedDeviceCount > 0 && SelectedFiles.Count > 0;
        }
    }

    [RelayCommand]
    private void AcceptIncomingTransfer() => _pendingIncomingApproval?.TrySetResult(true);

    [RelayCommand]
    private void RejectIncomingTransfer() => _pendingIncomingApproval?.TrySetResult(false);

    private void OnPeerConnected(string peerId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = $"Securely paired · {peerId}";
            PairedDeviceCount = 1;
            CanSendFiles = SelectedFiles.Count > 0 && !IsPairingBusy;
            CanClipboardSync = !OperatingSystem.IsBrowser();
            ClipboardSyncStatus = IsClipboardSyncEnabled
                ? "Paired · checking for clipboard changes…"
                : "Paired · enable sync on both devices to share text.";
            UpdateClipboardSyncTimer();
        });
    }

    private void UpdateClipboardSyncTimer()
    {
        if (IsClipboardSyncEnabled && CanClipboardSync && PairedDeviceCount > 0 && _clipboard is not null)
        {
            if (!_clipboardTimer.IsEnabled)
            {
                _captureClipboardBaseline = true;
                _clipboardTimer.Start();
            }
        }
        else
        {
            _clipboardTimer.Stop();
        }
    }

    private async void OnClipboardTimerTick(object? sender, EventArgs e)
    {
        if (!IsClipboardSyncEnabled ||
            PairedDeviceCount == 0 ||
            _clipboard is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _clipboardPollInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            var text = await _clipboard.TryGetTextAsync();
            if (!IsClipboardSyncEnabled || _clipboard is null || PairedDeviceCount == 0)
            {
                return;
            }

            _lastClipboardReadError = null;
            if (_captureClipboardBaseline)
            {
                _lastObservedClipboardText = text;
                _captureClipboardBaseline = false;
                ClipboardSyncStatus = "Clipboard sync is on · waiting for a new text copy.";
                return;
            }

            if (string.IsNullOrEmpty(text) || text == _lastObservedClipboardText)
            {
                _lastObservedClipboardText = text;
                return;
            }

            ShowClipboardPreview(text);
            _lastObservedClipboardText = text;
            if (System.Text.Encoding.UTF8.GetByteCount(text) > ClipboardSyncProtocol.MaximumTextBytes)
            {
                ClipboardSyncStatus = $"Clipboard text exceeds the {ClipboardSyncProtocol.MaximumTextBytes / 1024} KiB sync limit.";
                return;
            }

            var accepted = await _pairingCoordinator.SendClipboardAsync(text);
            ClipboardSyncStatus = accepted
                ? "Clipboard synced securely."
                : "The other device has clipboard sync turned off.";
        }
        catch (Exception exception)
        {
            if (!string.Equals(_lastClipboardReadError, exception.Message, StringComparison.Ordinal))
            {
                ClipboardSyncStatus = $"Clipboard sync failed: {exception.Message}";
                _lastClipboardReadError = exception.Message;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _clipboardPollInProgress, 0);
        }
    }

    private Task<bool> ApplyIncomingClipboardAsync(string text, CancellationToken cancellationToken)
    {
        if (!IsClipboardSyncEnabled || _clipboard is null || string.IsNullOrEmpty(text))
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (!IsClipboardSyncEnabled || _clipboard is null)
                {
                    completion.TrySetResult(false);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                _lastObservedClipboardText = text;
                _captureClipboardBaseline = false;
                await _clipboard.SetTextAsync(text);
                ShowClipboardPreview(text);
                ClipboardSyncStatus = "Received clipboard text securely.";
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                ClipboardSyncStatus = $"Could not update the clipboard: {exception.Message}";
                completion.TrySetException(exception);
            }
        });

        return AwaitClipboardWriteAsync(completion, cancellationToken);
    }

    private static async Task<bool> AwaitClipboardWriteAsync(
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    private void OnTransferProgressChanged(FileTransferProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var direction = progress.IsIncoming ? "Receiving" : "Sending";
            TransferStatus = $"{direction} {progress.FileName} · {FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes)}";
        });
    }

    private void OnFileReceived(string path)
    {
        Dispatcher.UIThread.Post(() => TransferStatus = $"Received {Path.GetFileName(path)} · saved to {path}");
    }

    private async Task<bool> ApproveIncomingTransferAsync(IncomingFileRequest request, CancellationToken cancellationToken)
    {
        await _incomingApprovalLock.WaitAsync(cancellationToken);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            _pendingIncomingApproval = completion;
            IncomingTransferPrompt =
                $"{request.FileName} · {FormatBytes(request.Length)} · from {request.PeerId}";
            HasIncomingTransferRequest = true;
        });

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task;
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_pendingIncomingApproval, completion))
                {
                    _pendingIncomingApproval = null;
                    HasIncomingTransferRequest = false;
                }
            });
            _incomingApprovalLock.Release();
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes < 1024 * 1024
            ? $"{bytes / 1024.0:0} KB"
            : $"{bytes / (1024.0 * 1024.0):0.0} MB";

    public sealed record SelectedFile(IStorageFile File)
    {
        public string Name => File.Name;
    }
}
