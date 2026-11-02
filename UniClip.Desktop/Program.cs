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
        PairingServerStartupStatus.SetFirewallWarning(FirewallSetup.Configure());
        var rendezvousApplication = RendezvousHost.CreateApplication(
            ["--urls", "http://0.0.0.0:5180"]);
        var rendezvousStarted = false;
        try
        {
            rendezvousApplication.StartAsync().GetAwaiter().GetResult();
            rendezvousStarted = true;
            PairingServerStartupStatus.Set(
                "PIN server ready · preparing your PIN…");
        }
        catch (Exception exception)
        {
            PairingServerStartupStatus.Set(
                $"PIN server could not start: {exception.Message}");
        }

        try
        {
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

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
