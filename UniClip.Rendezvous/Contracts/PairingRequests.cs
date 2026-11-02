using System.ComponentModel.DataAnnotations;

namespace UniClip.Rendezvous.Contracts;

/// <summary>Registers a temporary peer invite for PIN-based pairing.</summary>
public sealed record CreatePairingRequest
{
    /// <summary>The encoded peer invite to make available to the matching PIN.</summary>
    [Required, MaxLength(8192)]
    public required string Invite { get; init; }
}
