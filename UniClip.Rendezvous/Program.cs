using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using UniClip.Rendezvous.Endpoints;
using UniClip.Rendezvous.Services;

namespace UniClip.Rendezvous;

public static class Program
{
    public static Task Main(string[] args) =>
        RendezvousHost.CreateApplication(args).RunAsync();
}

public static class RendezvousHost
{
    public static WebApplication CreateApplication(string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddValidation();
        builder.Services.AddSingleton<IPairingStore, PairingStore>();
        builder.Services.AddHostedService<LanDiscoveryService>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("pairing", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 6,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.MapPairingEndpoints();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        return app;
    }
}
