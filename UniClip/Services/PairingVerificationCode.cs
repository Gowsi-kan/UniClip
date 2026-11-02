using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UniClip.Services;

public static class PairingVerificationCode
{
    public static string Create(string peerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(peerId));
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000;
        var digits = value.ToString("D6", CultureInfo.InvariantCulture);
        return $"{digits[..3]} {digits[3..]}";
    }
}
