using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UniClip.Rendezvous.Services;

public sealed class LanDiscoveryService(ILogger<LanDiscoveryService> logger) : IHostedService, IDisposable
{
    public const int Port = 5181;
    public const string DiscoveryRequest = "UNICLIP_DISCOVER_V1";
    public const string DiscoveryResponse = "UNICLIP_RENDEZVOUS_V1";

    private CancellationTokenSource? _stopping;
    private Task? _listenerTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _listenerTask = Task.Run(() => Listen(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping?.Cancel();
        if (_listenerTask is not null)
        {
            await _listenerTask.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        _stopping?.Cancel();
        _stopping?.Dispose();
    }

    private void Listen(CancellationToken stoppingToken)
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        var requestBytes = Encoding.ASCII.GetBytes(DiscoveryRequest);
        var responseBytes = Encoding.ASCII.GetBytes(DiscoveryResponse);

        logger.LogInformation("LAN pairing discovery listening on UDP port {Port}", Port);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!listener.Client.Poll(100_000, SelectMode.SelectRead))
            {
                continue;
            }

            var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            var request = listener.Receive(ref remoteEndPoint);
            if (request.AsSpan().SequenceEqual(requestBytes))
            {
                listener.Send(responseBytes, responseBytes.Length, remoteEndPoint);
            }
        }

        logger.LogInformation("LAN pairing discovery stopped.");
    }
}
