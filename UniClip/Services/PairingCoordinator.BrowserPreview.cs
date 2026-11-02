using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UniClip.Services;

public sealed record FileTransferProgress(string FileName, long BytesTransferred, long TotalBytes, bool IsIncoming);

public sealed record IncomingFileRequest(string PeerId, string FileName, long Length);

public sealed record PairingInvite
{
    public string PeerId { get; init; } = string.Empty;

    public string Encode() =>
        throw new NotSupportedException("Pairing and transfers are unavailable in the browser preview.");
}

public sealed class PairingCoordinator : IAsyncDisposable
{
    private static NotSupportedException CreateBrowserPreviewException() =>
        new("Pairing and transfers are unavailable in the browser preview.");

    public event Action<string>? PeerConnected
    {
        add { }
        remove { }
    }

    public event Action<FileTransferProgress>? TransferProgressChanged
    {
        add { }
        remove { }
    }

    public event Action<string>? FileReceived
    {
        add { }
        remove { }
    }

    public event Func<IncomingFileRequest, CancellationToken, Task<bool>>? IncomingTransferApprovalRequested
    {
        add { }
        remove { }
    }

    public event Func<string, CancellationToken, Task<bool>>? IncomingClipboardRequested
    {
        add { }
        remove { }
    }

    public Task<PairingInvite> CreateInviteAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<PairingInvite>(CreateBrowserPreviewException());

    public Task<string> CreatePairingCodeAsync(
        PairingInvite invite,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(CreateBrowserPreviewException());

    public Task<string> ClaimPairingInviteAsync(
        string pin,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(CreateBrowserPreviewException());

    public Task<string> ConnectAsync(string encodedInvite, CancellationToken cancellationToken = default) =>
        Task.FromException<string>(CreateBrowserPreviewException());

    public Task<string> SendFileAsync(
        Stream source,
        string fileName,
        long length,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(CreateBrowserPreviewException());

    public Task<bool> SendClipboardAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(CreateBrowserPreviewException());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
