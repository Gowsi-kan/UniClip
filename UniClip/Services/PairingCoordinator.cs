using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace UniClip.Services;

public sealed class PairingCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private ServiceProvider? _serviceProvider;
    private ILocalPeer? _localPeer;
    private ISession? _pairedSession;
    private FileTransferProtocol? _fileTransferProtocol;
    private ClipboardSyncProtocol? _clipboardSyncProtocol;
    private readonly string? _receiveDirectory;
    private bool _disposed;

    public PairingCoordinator(string? receiveDirectory = null)
    {
        _receiveDirectory = receiveDirectory;
    }

    public event Action<string>? PeerConnected;
    public event Action<FileTransferProgress>? TransferProgressChanged;
    public event Action<string>? FileReceived;
    public event Func<IncomingFileRequest, CancellationToken, Task<bool>>? IncomingTransferApprovalRequested;
    public event Func<string, CancellationToken, Task<bool>>? IncomingClipboardRequested;

    public async Task<PairingInvite> CreateInviteAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);

        var peer = _localPeer ?? throw new InvalidOperationException("The local peer is not available.");
        return PairingInvite.Create(peer.Identity.PeerId, GetInviteAddresses(peer));
    }

    public async Task<string> CreatePairingCodeAsync(
        PairingInvite invite,
        CancellationToken cancellationToken = default) =>
        await RendezvousPairingClient.RegisterAsync(invite.Encode(), cancellationToken);

    public Task<string> ClaimPairingInviteAsync(
        string pin,
        CancellationToken cancellationToken = default) =>
        RendezvousPairingClient.ClaimAsync(pin, cancellationToken);

    public async Task<string> ConnectAsync(string encodedInvite, CancellationToken cancellationToken = default)
    {
        var invite = PairingInvite.Decode(encodedInvite);
        await EnsureStartedAsync(cancellationToken);
        var peer = _localPeer ?? throw new InvalidOperationException("The local peer is not available.");

        var addresses = invite.Addresses.Select(Multiaddress.Decode).ToArray();
        ISession session;
        try
        {
            session = await peer.DialAsync(addresses, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The PIN was accepted, but the direct device connection failed. Allow UniClip through the private-network firewall on both PCs, make sure the Wi-Fi does not isolate devices, and create a new PIN because this one has been used. Connection details: {exception.Message}",
                exception);
        }

        if (session.RemotePeerId.ToString() != invite.PeerId)
        {
            await session.DisconnectAsync();
            throw new InvalidOperationException("The connected device did not match the identity in the pairing invite.");
        }

        if (_pairedSession is not null && !ReferenceEquals(_pairedSession, session))
        {
            await _pairedSession.DisconnectAsync();
        }

        _pairedSession = session;
        return invite.PeerId;
    }

    public async Task<string> SendFileAsync(
        Stream source,
        string fileName,
        long length,
        CancellationToken cancellationToken = default)
    {
        var session = _pairedSession ?? throw new InvalidOperationException("Pair with a device before sending files.");
        var fileTransfer = _fileTransferProtocol ?? throw new InvalidOperationException("The file-transfer protocol is not available.");
        return await fileTransfer.SendAsync(session, source, fileName, length, cancellationToken);
    }

    public async Task<bool> SendClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        var session = _pairedSession ?? throw new InvalidOperationException("Pair with a device before syncing clipboard text.");
        var clipboardSync = _clipboardSyncProtocol ?? throw new InvalidOperationException("The clipboard-sync protocol is not available.");
        return await clipboardSync.SendAsync(session, text, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pairedSession is not null)
        {
            await _pairedSession.DisconnectAsync();
            _pairedSession = null;
        }

        if (_localPeer is not null)
        {
            await _localPeer.DisposeAsync();
            _localPeer = null;
        }

        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null;
        }

        _startLock.Dispose();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_localPeer is not null)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_localPeer is not null)
            {
                return;
            }

            var fileTransfer = new FileTransferProtocol(_receiveDirectory ?? GetReceiveDirectory());
            fileTransfer.ProgressChanged += progress => TransferProgressChanged?.Invoke(progress);
            fileTransfer.FileReceived += path => FileReceived?.Invoke(path);
            fileTransfer.ApprovalHandler = (request, token) =>
                IncomingTransferApprovalRequested?.Invoke(request, token) ?? Task.FromResult(false);
            var clipboardSync = new ClipboardSyncProtocol
            {
                IncomingClipboardHandler = (text, token) =>
                    IncomingClipboardRequested?.Invoke(text, token) ?? Task.FromResult(false)
            };
            var serviceProvider = new ServiceCollection()
                .AddLibp2p(builder => builder.AddProtocol(fileTransfer).AddProtocol(clipboardSync))
                .BuildServiceProvider();
            var localPeer = serviceProvider.GetRequiredService<IPeerFactory>().Create();
            localPeer.OnConnected += session =>
            {
                _pairedSession = session;
                PeerConnected?.Invoke(session.RemotePeerId.ToString());
            };
            try
            {
                var listenAddress = Multiaddress.Decode("/ip4/0.0.0.0/tcp/5182");
                await localPeer.StartListenAsync([listenAddress], cancellationToken);
                if (localPeer.ListenAddresses.Count == 0)
                {
                    throw new InvalidOperationException("No network listener could be started. Check network permissions and try again.");
                }

                var discovery = serviceProvider.GetRequiredService<MDnsDiscoveryProtocol>();
                await discovery.StartDiscoveryAsync(localPeer.ListenAddresses, cancellationToken);
                _serviceProvider = serviceProvider;
                _localPeer = localPeer;
                _fileTransferProtocol = fileTransfer;
                _clipboardSyncProtocol = clipboardSync;
            }
            catch
            {
                await localPeer.DisposeAsync();
                await serviceProvider.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private static IEnumerable<Multiaddress> GetInviteAddresses(ILocalPeer peer)
    {
        var localIpv4Addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicastAddress => unicastAddress.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                              !IPAddress.IsLoopback(address) &&
                              IsPrivateLanAddress(address))
            .Distinct()
            .ToArray();

        foreach (var listenAddress in peer.ListenAddresses)
        {
            var address = listenAddress.ToString();
            if (address.StartsWith("/ip4/0.0.0.0/", StringComparison.Ordinal))
            {
                foreach (var localAddress in localIpv4Addresses)
                {
                    yield return Multiaddress.Decode(address.Replace("/ip4/0.0.0.0/", $"/ip4/{localAddress}/", StringComparison.Ordinal));
                }
            }
            else if (!address.StartsWith("/ip6/", StringComparison.Ordinal))
            {
                yield return listenAddress;
            }
        }

    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private static string GetReceiveDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = string.IsNullOrWhiteSpace(userProfile) ? string.Empty : Path.Combine(userProfile, "Downloads");
        if (Directory.Exists(downloads))
        {
            return downloads;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(appData) ? Path.GetTempPath() : appData;
        return Path.Combine(root, "UniClip", "Received");
    }
}
