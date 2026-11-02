using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UniClip.Services;

public static class RendezvousPairingClient
{
    private const int DiscoveryPort = 5181;
    private const string DiscoveryRequest = "UNICLIP_DISCOVER_V1";
    private const string DiscoveryResponse = "UNICLIP_RENDEZVOUS_V1";
    private const uint MaximumUnicastProbeHosts = 512;

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<string> RegisterAsync(
        string invite,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri("http://127.0.0.1:5180/api/pairings/");
        using var response = await Client.PostAsJsonAsync(
            uri,
            new CreatePairingRequest(invite),
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("The PIN server is rate-limiting requests. Wait a minute and try again.");
        }

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CreatePairingResponse>(cancellationToken);
        if (result is null || string.IsNullOrEmpty(result.Pin) || result.Pin.Length != 6 ||
            result.Pin.Any(character => character is < '0' or > '9'))
        {
            throw new InvalidOperationException("The PIN server returned an invalid pairing code.");
        }

        return result.Pin;
    }

    public static async Task<string> ClaimAsync(
        string pin,
        CancellationToken cancellationToken = default)
    {
        var normalizedPin = PairingPin.Normalize(pin);
        var servers = await DiscoverServersAsync(cancellationToken);
        if (servers.Count == 0)
        {
            throw new InvalidOperationException(
                "No UniClip PIN server was found. Put both PCs on the same Wi-Fi network and allow inbound UDP port 5181 through the firewall on both PCs. Public Wi-Fi may block device-to-device traffic; check for client isolation.");
        }

        var reachedServer = false;
        var connectionErrors = new List<string>();
        foreach (var server in servers)
        {
            HttpResponseMessage response;
            try
            {
                response = await Client.PostAsync(
                    new Uri($"http://{server}:5180/api/pairings/{normalizedPin}/claim"),
                    content: null,
                    cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                connectionErrors.Add($"{server}: {exception.Message}");
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                connectionErrors.Add($"{server}: connection timed out.");
                continue;
            }

            reachedServer = true;
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new InvalidOperationException("Too many PIN attempts. Wait a minute and try again.");
                }

                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<ClaimPairingResponse>(cancellationToken);
                if (string.IsNullOrWhiteSpace(result?.Invite))
                {
                    throw new InvalidOperationException("The PIN server returned an empty pairing invite.");
                }

                return result.Invite;
            }
        }

        if (!reachedServer)
        {
            throw new InvalidOperationException(
                $"UniClip found another device, but could not contact its PIN server on TCP port 5180. Allow inbound TCP 5180 on the PC showing the PIN. Connection details: {string.Join("; ", connectionErrors)}");
        }

        throw new InvalidOperationException(
            "The PIN was not found, has expired, or was already used. Generate a new PIN on the other PC and try again.");
    }

    private static async Task<IReadOnlyList<IPAddress>> DiscoverServersAsync(
    CancellationToken cancellationToken)
    {
        const string Tag = "[Discovery]";
        var sw = Stopwatch.StartNew();

        using var socket = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        Debug.WriteLine($"{Tag} Socket bound to {socket.Client.LocalEndPoint}, broadcast={socket.EnableBroadcast}");

        var request = Encoding.ASCII.GetBytes(DiscoveryRequest);
        var targets = GetDiscoveryTargets().ToArray();
        Debug.WriteLine($"{Tag} Request=\"{DiscoveryRequest}\" ({request.Length} bytes), port={DiscoveryPort}, targets={targets.Length}");

        foreach (var target in targets)
        {
            try
            {
                var sent = await socket.SendAsync(
                    request,
                    new IPEndPoint(target, DiscoveryPort),
                    cancellationToken);
                Debug.WriteLine($"{Tag} [{sw.ElapsedMilliseconds} ms] Sent {sent} bytes -> {target}:{DiscoveryPort}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"{Tag} [{sw.ElapsedMilliseconds} ms] Send FAILED -> {target}: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        Debug.WriteLine($"{Tag} [{sw.ElapsedMilliseconds} ms] Waiting for responses (2 s window)...");

        var servers = new HashSet<IPAddress>();
        var responseBytes = Encoding.ASCII.GetBytes(DiscoveryResponse);
        var received = 0;

        while (!discoveryTimeout.IsCancellationRequested)
        {
            try
            {
                var response = await socket.ReceiveAsync(discoveryTimeout.Token);
                received++;
                var remote = response.RemoteEndPoint;
                var matches = response.Buffer.AsSpan().SequenceEqual(responseBytes);
                var isLocal = IsLocalAddress(remote.Address);

                Debug.WriteLine(
                    $"{Tag} [{sw.ElapsedMilliseconds} ms] RX #{received} from {remote}, {response.Buffer.Length} bytes, match={matches}, local={isLocal}");

                if (!matches)
                {
                    var preview = response.Buffer.AsSpan(0, Math.Min(response.Buffer.Length, 64));
                    Debug.WriteLine($"{Tag}   mismatch payload hex={Convert.ToHexString(preview)} ascii=\"{Encoding.ASCII.GetString(preview)}\"");
                }
                else if (isLocal)
                {
                    Debug.WriteLine($"{Tag}   ignored (own address)");
                }
                else if (servers.Add(remote.Address))
                {
                    Debug.WriteLine($"{Tag}   server added: {remote.Address}");
                }
                else
                {
                    Debug.WriteLine($"{Tag}   duplicate: {remote.Address}");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine($"{Tag} [{sw.ElapsedMilliseconds} ms] Discovery window elapsed");
                break;
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"{Tag} [{sw.ElapsedMilliseconds} ms] Receive SocketException {ex.SocketErrorCode} ({ex.ErrorCode}): {ex.Message}");
                throw;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine($"{Tag} [{sw.ElapsedMilliseconds} ms] Cancelled by caller");
        }
        cancellationToken.ThrowIfCancellationRequested();

        Debug.WriteLine(
            $"{Tag} Done in {sw.ElapsedMilliseconds} ms: {received} packets, {servers.Count} server(s): [{string.Join(", ", servers)}]");
        return servers.ToArray();
    }

    private static IReadOnlyCollection<IPAddress> GetDiscoveryTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                    address.IPv4Mask is null)
                {
                    continue;
                }

                var addressBytes = address.Address.GetAddressBytes();
                var maskBytes = address.IPv4Mask.GetAddressBytes();
                var addressValue = BinaryPrimitives.ReadUInt32BigEndian(addressBytes);
                var maskValue = BinaryPrimitives.ReadUInt32BigEndian(maskBytes);
                var networkValue = addressValue & maskValue;
                var broadcastValue = networkValue | ~maskValue;
                var broadcastBytes = new byte[sizeof(uint)];
                BinaryPrimitives.WriteUInt32BigEndian(broadcastBytes, broadcastValue);
                targets.Add(new IPAddress(broadcastBytes));

                var addressCount = broadcastValue - networkValue;
                var hostCount = addressCount > 1 ? addressCount - 1 : 0;
                if (hostCount is 0 or > MaximumUnicastProbeHosts)
                {
                    continue;
                }

                for (var candidate = networkValue + 1; candidate < broadcastValue; candidate++)
                {
                    if (candidate == addressValue)
                    {
                        continue;
                    }

                    var candidateBytes = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32BigEndian(candidateBytes, candidate);
                    var candidateAddress = new IPAddress(candidateBytes);
                    if (IsPrivateLanAddress(candidateAddress))
                    {
                        targets.Add(candidateAddress);
                    }
                }
            }
        }

        return targets;
    }

    private static bool IsLocalAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private sealed record CreatePairingRequest(string Invite);

    private sealed record CreatePairingResponse(string Pin, DateTimeOffset ExpiresAt);

    private sealed record ClaimPairingResponse(string Invite);
}
