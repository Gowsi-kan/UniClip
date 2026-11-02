using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace UniClip.Rendezvous.Services;

public sealed class PairingStore : IPairingStore
{
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, PairingEntry> _entries = new(StringComparer.Ordinal);

    public bool TryCreate(string invite, out string pin, out DateTimeOffset expiresAt)
    {
        RemoveExpiredEntries();
        expiresAt = DateTimeOffset.UtcNow.Add(PairingLifetime);

        for (var attempt = 0; attempt < 32; attempt++)
        {
            pin = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            if (_entries.TryAdd(pin, new PairingEntry(invite, expiresAt)))
            {
                return true;
            }
        }

        pin = string.Empty;
        return false;
    }

    public bool TryClaim(string pin, out string invite)
    {
        invite = string.Empty;
        if (!_entries.TryGetValue(pin, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(new KeyValuePair<string, PairingEntry>(pin, entry));
            return false;
        }

        if (!_entries.TryRemove(new KeyValuePair<string, PairingEntry>(pin, entry)))
        {
            return false;
        }

        invite = entry.Invite;
        return true;
    }

    private void RemoveExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (pin, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(new KeyValuePair<string, PairingEntry>(pin, entry));
            }
        }
    }

    private sealed record PairingEntry(string Invite, DateTimeOffset ExpiresAt);
}
