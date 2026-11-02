using System;
using System.Linq;

namespace UniClip.Services;

public static class PairingPin
{
    public static string Normalize(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var normalized = pin.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != 6 || normalized.Any(character => character is < '0' or > '9'))
        {
            throw new FormatException("Enter the six-digit PIN shown on the other device.");
        }

        return normalized;
    }
}
