namespace UniClip.Rendezvous.Contracts;

/// <summary>Contains the PIN assigned to a temporary pairing.</summary>
public sealed record CreatePairingResponse(string Pin, DateTimeOffset ExpiresAt);

/// <summary>Contains the peer invite retrieved by claiming a PIN.</summary>
public sealed record ClaimPairingResponse(string Invite);
