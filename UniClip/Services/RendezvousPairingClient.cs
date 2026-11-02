using System;
using System.Collections.Generic;
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
                "No UniClip PIN server was found on the local network. Make sure both devices are on the same network and allow UniClip through the private-network firewall.");
        }

        foreach (var server in servers)
        {
            using var response = await Client.PostAsync(
                new Uri($"http://{server}:5180/api/pairings/{normalizedPin}/claim"),
                content: null,
                cancellationToken);

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

        throw new InvalidOperationException(
            "PIN not found, expired, or already used. Ask the other device to create a new PIN.");
    }

    private static async Task<IReadOnlyList<IPAddress>> DiscoverServersAsync(
        CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var request = Encoding.ASCII.GetBytes(DiscoveryRequest);
        var targets = GetBroadcastAddresses().Append(IPAddress.Broadcast).Distinct();
        foreach (var target in targets)
        {
            await socket.SendAsync(
                request,
                new IPEndPoint(target, DiscoveryPort),
                cancellationToken);
        }

        using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        var servers = new HashSet<IPAddress>();
        var responseBytes = Encoding.ASCII.GetBytes(DiscoveryResponse);
        while (!discoveryTimeout.IsCancellationRequested)
        {
            try
            {
                var response = await socket.ReceiveAsync(discoveryTimeout.Token);
                if (response.Buffer.AsSpan().SequenceEqual(responseBytes) &&
                    !IsLocalAddress(response.RemoteEndPoint.Address))
                {
                    servers.Add(response.RemoteEndPoint.Address);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return servers.ToArray();
    }

    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
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
                for (var index = 0; index < addressBytes.Length; index++)
                {
                    addressBytes[index] = (byte)(addressBytes[index] | ~maskBytes[index]);
                }

                yield return new IPAddress(addressBytes);
            }
        }
    }

    private static bool IsLocalAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));

    private sealed record CreatePairingRequest(string Invite);

    private sealed record CreatePairingResponse(string Pin, DateTimeOffset ExpiresAt);

    private sealed record ClaimPairingResponse(string Invite);
}
