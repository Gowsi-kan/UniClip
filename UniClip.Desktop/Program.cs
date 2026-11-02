using System;
using System.Threading;
using Avalonia;
using Microsoft.AspNetCore.Builder;
using UniClip.Rendezvous;
using UniClip.Services;

namespace UniClip.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        SessionDiagnostics.StartSession();
        try
        {
            SessionDiagnostics.Write("Startup", "Configuring firewall rules.");
            PairingServerStartupStatus.SetFirewallWarning(FirewallSetup.Configure());
            SessionDiagnostics.Write(
                "Startup",
                PairingServerStartupStatus.FirewallWarning is null
                    ? "Firewall configuration completed without a warning."
                    : $"Firewall warning: {PairingServerStartupStatus.FirewallWarning}");

            var rendezvousApplication = RendezvousHost.CreateApplication(
                ["--urls", "http://0.0.0.0:5180"],
                new SessionFileLoggerProvider());
            var rendezvousStarted = false;
            try
            {
                rendezvousApplication.StartAsync().GetAwaiter().GetResult();
                rendezvousStarted = true;
                PairingServerStartupStatus.Set(
                    "PIN server ready · preparing your PIN…");
                SessionDiagnostics.Write("Startup", "Rendezvous HTTP service started on TCP port 5180.");
            }
            catch (Exception exception)
            {
                PairingServerStartupStatus.Set(
                    $"PIN server could not start: {exception.Message}");
                SessionDiagnostics.Write("Startup", "Rendezvous HTTP service failed to start.", exception);
            }

            try
            {
                SessionDiagnostics.Write("Startup", "Starting Avalonia desktop lifetime.");
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                if (rendezvousStarted)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    rendezvousApplication.StopAsync(timeout.Token).GetAwaiter().GetResult();
                }

                rendezvousApplication.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            SessionDiagnostics.Write("Startup", "Desktop application terminated with an unhandled exception.", exception);
            throw;
        }
        finally
        {
            SessionDiagnostics.EndSession();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
