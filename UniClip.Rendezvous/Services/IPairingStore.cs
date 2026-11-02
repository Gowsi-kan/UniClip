namespace UniClip.Rendezvous.Services;

public interface IPairingStore
{
    bool TryCreate(string invite, out string pin, out DateTimeOffset expiresAt);

    bool TryClaim(string pin, out string invite);
}
