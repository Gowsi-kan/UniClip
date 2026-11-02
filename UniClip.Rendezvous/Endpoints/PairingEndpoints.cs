using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UniClip.Rendezvous.Contracts;
using UniClip.Rendezvous.Services;

namespace UniClip.Rendezvous.Endpoints;

public static class PairingEndpoints
{
    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/pairings").WithTags("Pairing");

        group.MapPost("/", Create)
            .WithName("CreatePairing")
            .WithSummary("Create a temporary PIN pairing")
            .WithDescription("Stores a peer invite in memory and returns a random six-digit PIN that expires after ten minutes.")
            .Produces<CreatePairingResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting("pairing");

        group.MapPost("/{pin}/claim", Claim)
            .WithName("ClaimPairing")
            .WithSummary("Claim a temporary PIN pairing")
            .WithDescription("Returns and removes the invite associated with a valid six-digit PIN.")
            .Produces<ClaimPairingResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting("pairing");

        return endpoints;
    }

    private static Results<Created<CreatePairingResponse>, Conflict<ProblemDetails>> Create(
        CreatePairingRequest request,
        IPairingStore pairingStore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Invite))
        {
            return TypedResults.Conflict(new ProblemDetails
            {
                Title = "Pairing could not be created",
                Detail = "The peer invite is empty."
            });
        }

        if (!pairingStore.TryCreate(request.Invite, out var pin, out var expiresAt))
        {
            return TypedResults.Conflict(new ProblemDetails
            {
                Title = "Pairing could not be created",
                Detail = "A unique PIN could not be allocated. Try again."
            });
        }

        var response = new CreatePairingResponse(pin, expiresAt);
        return TypedResults.Created($"/api/pairings/{pin}/claim", response);
    }

    private static Results<Ok<ClaimPairingResponse>, NotFound<ProblemDetails>> Claim(
        string pin,
        IPairingStore pairingStore)
    {
        if (pin.Length != 6 || pin.Any(character => character is < '0' or > '9') ||
            !pairingStore.TryClaim(pin, out var invite))
        {
            return TypedResults.NotFound(new ProblemDetails
            {
                Title = "PIN not found",
                Detail = "The PIN is invalid, expired, or has already been used. Ask the other device for a new PIN."
            });
        }

        return TypedResults.Ok(new ClaimPairingResponse(invite));
    }
}
