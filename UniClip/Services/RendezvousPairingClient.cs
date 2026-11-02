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
        SessionDiagnostics.Write("Pairing", "Registering an invite with the local rendezvous service; invite contents omitted.");
        using var response = await Client.PostAsJsonAsync(
            uri,
            new CreatePairingRequest(invite),
            cancellationToken);
        SessionDiagnostics.Write("Pairing", $"Local rendezvous registration returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");

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

        SessionDiagnostics.Write("Pairing", "Local rendezvous registration succeeded; PIN value omitted.");
        return result.Pin;
    }

    public static async Task<string> ClaimAsync(
        string pin,
        CancellationToken cancellationToken = default)
    {
        var normalizedPin = PairingPin.Normalize(pin);
        SessionDiagnostics.Write("Pairing", "PIN claim started; PIN value omitted.");
        var servers = await DiscoverServersAsync(cancellationToken);
        if (servers.Count == 0)
        {
            SessionDiagnostics.Write("Pairing", "PIN claim stopped because LAN discovery found no remote servers.");
            throw new InvalidOperationException(
                "No UniClip PIN server was found. Put both PCs on the same Wi-Fi network and allow inbound UDP port 5181 through the firewall on both PCs. Public Wi-Fi may block device-to-device traffic; check for client isolation.");
        }

        SessionDiagnostics.Write("Pairing", $"LAN discovery found {servers.Count} remote server(s): {string.Join(", ", servers)}.");
        var reachedServer = false;
        var connectionErrors = new List<string>();
        foreach (var server in servers)
        {
            SessionDiagnostics.Write("Pairing", $"Claiming PIN from {server}:5180; PIN value omitted.");
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
                SessionDiagnostics.Write("Pairing", $"HTTP claim request to {server}:5180 failed.", exception);
                connectionErrors.Add($"{server}: {exception.Message}");
                continue;
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                SessionDiagnostics.Write("Pairing", $"HTTP claim request to {server}:5180 timed out.", exception);
                connectionErrors.Add($"{server}: connection timed out.");
                continue;
            }

            reachedServer = true;
            SessionDiagnostics.Write("Pairing", $"HTTP claim response from {server}:5180: {(int)response.StatusCode} ({response.StatusCode}).");
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

                SessionDiagnostics.Write("Pairing", $"PIN claim succeeded at {server}; invite contents omitted.");
                return result.Invite;
            }
        }

        if (!reachedServer)
        {
            SessionDiagnostics.Write("Pairing", "LAN discovery succeeded, but no HTTP PIN server was reachable on TCP port 5180.");
            throw new InvalidOperationException(
                $"UniClip found another device, but could not contact its PIN server on TCP port 5180. Allow inbound TCP 5180 on the PC showing the PIN. Connection details: {string.Join("; ", connectionErrors)}");
        }

        SessionDiagnostics.Write("Pairing", "No discovered server accepted the PIN; it may be invalid, expired, or already claimed.");
        throw new InvalidOperationException(
            "The PIN was not found, has expired, or was already used. Generate a new PIN on the other PC and try again.");
    }

    private static async Task<IReadOnlyList<IPAddress>> DiscoverServersAsync(
    CancellationToken cancellationToken)
    {
        const string Tag = "[Discovery]";
        var sw = Stopwatch.StartNew();

        SessionDiagnostics.Write(Tag, "Opening IPv4 UDP discovery socket.");
        using var socket = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        SessionDiagnostics.Write(Tag, $"Socket bound to {socket.Client.LocalEndPoint}, broadcast={socket.EnableBroadcast}.");

        var request = Encoding.ASCII.GetBytes(DiscoveryRequest);
        var targets = GetDiscoveryTargets().ToArray();
        SessionDiagnostics.Write(Tag, $"Request={DiscoveryRequest} ({request.Length} bytes), port={DiscoveryPort}, target count={targets.Length}, targets=[{string.Join(", ", targets)}].");

        var sentTargets = 0;
        var sentBytes = 0;
        foreach (var target in targets)
        {
            try
            {
                var sent = await socket.SendAsync(
                    request,
                    new IPEndPoint(target, DiscoveryPort),
                    cancellationToken);
                sentTargets++;
                sentBytes += sent;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SessionDiagnostics.Write(Tag, $"[{sw.ElapsedMilliseconds} ms] Send failed -> {target}:{DiscoveryPort}.", ex);
                throw;
            }
        }

        SessionDiagnostics.Write(Tag, $"[{sw.ElapsedMilliseconds} ms] Sent {sentBytes} total bytes to {sentTargets}/{targets.Length} targets.");
        using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        SessionDiagnostics.Write(Tag, $"[{sw.ElapsedMilliseconds} ms] Waiting for responses (2 second window).");

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

                SessionDiagnostics.Write(
                    Tag,
                    $"[{sw.ElapsedMilliseconds} ms] RX #{received} from {remote}, {response.Buffer.Length} bytes, expected response={matches}.");

                if (!matches)
                {
                    var preview = response.Buffer.AsSpan(0, Math.Min(response.Buffer.Length, 64));
                    SessionDiagnostics.Write(Tag, $"Unexpected response preview hex={Convert.ToHexString(preview)} ascii=\"{Encoding.ASCII.GetString(preview)}\".");
                }
                else if (servers.Add(remote.Address))
                {
                    SessionDiagnostics.Write(Tag, $"Remote server added: {remote.Address}.");
                }
                else
                {
                    SessionDiagnostics.Write(Tag, $"Duplicate response from {remote.Address}.");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SessionDiagnostics.Write(Tag, $"[{sw.ElapsedMilliseconds} ms] Discovery window elapsed.");
                break;
            }
            catch (SocketException ex)
            {
                SessionDiagnostics.Write(Tag, $"[{sw.ElapsedMilliseconds} ms] Receive socket exception {ex.SocketErrorCode} ({ex.ErrorCode}).", ex);
                throw;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            SessionDiagnostics.Write(Tag, $"[{sw.ElapsedMilliseconds} ms] Cancelled by caller.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        SessionDiagnostics.Write(
            Tag,
            $"Done in {sw.ElapsedMilliseconds} ms: {received} packets, {servers.Count} server(s): [{string.Join(", ", servers)}].");
        return servers.ToArray();
    }

    private static IReadOnlyCollection<IPAddress> GetDiscoveryTargets()
    {
        var networks = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Select(network => network.GetIPProperties())
            .ToArray();
        foreach (var address in networks.SelectMany(properties => properties.UnicastAddresses))
        {
            if (address.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                SessionDiagnostics.Write(
                    "Discovery",
                    $"Active IPv4 address {address.Address} with mask {address.IPv4Mask?.ToString() ?? "unknown"}.");
            }
        }

        var localAddresses = networks
            .SelectMany(properties => properties.UnicastAddresses)
            .Select(unicast => unicast.Address)
            .ToHashSet();
        var targets = new HashSet<IPAddress>();

        foreach (var properties in networks)
        {
            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                    address.IPv4Mask is null ||
                    IPAddress.IsLoopback(address.Address) ||
                    !IsPrivateLanAddress(address.Address))
                {
                    continue;
                }

                var addressBytes = address.Address.GetAddressBytes();
                var maskBytes = address.IPv4Mask.GetAddressBytes();
                var addressValue = BinaryPrimitives.ReadUInt32BigEndian(addressBytes);
                var maskValue = BinaryPrimitives.ReadUInt32BigEndian(maskBytes);
                var networkValue = addressValue & maskValue;
                var broadcastValue = networkValue | ~maskValue;

                var addressCount = broadcastValue - networkValue;
                var hostCount = addressCount > 1 ? addressCount - 1 : 0;
                if (hostCount is 0 or > MaximumUnicastProbeHosts)
                {
                    SessionDiagnostics.Write(
                        "Discovery",
                        $"Skipping subnet {address.Address}/{address.IPv4Mask}: {hostCount} candidate hosts exceeds probe limit {MaximumUnicastProbeHosts}.");
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
                    if (IsPrivateLanAddress(candidateAddress) && !localAddresses.Contains(candidateAddress))
                    {
                        targets.Add(candidateAddress);
                    }
                }
            }
        }

        return targets;
    }

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
