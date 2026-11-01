using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Multiformats.Address;
using Nethermind.Libp2p.Core;

namespace UniClip.Services;

public sealed record PairingInvite(int Version, string PeerId, string[] Addresses, DateTimeOffset ExpiresAt)
{
    private const string Prefix = "unclip-pair:v1:";
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumAddressCount = 8;
    private const int MaximumInviteLength = 8192;

    public static PairingInvite Create(PeerId peerId, IEnumerable<Multiaddress> addresses)
    {
        var id = peerId.ToString();
        var encodedAddresses = addresses
            .Select(address => NormalizeAddress(address, id))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumAddressCount)
            .ToArray();

        if (encodedAddresses.Length == 0)
        {
            throw new InvalidOperationException("The listener did not provide any addresses for the pairing invite.");
        }

        return new PairingInvite(1, id, encodedAddresses, DateTimeOffset.UtcNow.Add(InviteLifetime));
    }

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        var encoded = Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Prefix + encoded;
    }

    public static PairingInvite Decode(string encodedInvite)
    {
        if (string.IsNullOrWhiteSpace(encodedInvite) || encodedInvite.Length > MaximumInviteLength ||
            !encodedInvite.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new FormatException("This is not a valid UniClip pairing invite.");
        }

        var token = encodedInvite[Prefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        token = token.PadRight(token.Length + ((4 - token.Length % 4) % 4), '=');

        PairingInvite? invite;
        try
        {
            invite = JsonSerializer.Deserialize<PairingInvite>(Convert.FromBase64String(token));
        }
        catch (FormatException exception)
        {
            throw new FormatException("The pairing invite is malformed.", exception);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The pairing invite is malformed.", exception);
        }

        if (invite is null || invite.Version != 1 || string.IsNullOrWhiteSpace(invite.PeerId) ||
            invite.Addresses is null || invite.Addresses.Length is 0 or > MaximumAddressCount)
        {
            throw new FormatException("The pairing invite is incomplete or uses an unsupported version.");
        }

        var timeRemaining = invite.ExpiresAt - DateTimeOffset.UtcNow;
        if (timeRemaining <= TimeSpan.Zero)
        {
            throw new FormatException("This pairing invite has expired. Create a new invite on the other device.");
        }

        if (timeRemaining > InviteLifetime + TimeSpan.FromMinutes(1))
        {
            throw new FormatException("The pairing invite has an invalid expiration time.");
        }

        var expectedPeerId = new PeerId(invite.PeerId);
        foreach (var addressText in invite.Addresses)
        {
            var address = Multiaddress.Decode(addressText);
            if (address.GetPeerId() != expectedPeerId)
            {
                throw new FormatException("The pairing invite contains an address for a different device.");
            }
        }

        return invite;
    }

    private static string NormalizeAddress(Multiaddress address, string peerId)
    {
        var addressPeerId = address.GetPeerId();
        if (addressPeerId is not null && addressPeerId.ToString() != peerId)
        {
            throw new InvalidOperationException("The listener returned an address for a different peer.");
        }

        return addressPeerId is null ? $"{address}/p2p/{peerId}" : address.ToString();
    }
}
