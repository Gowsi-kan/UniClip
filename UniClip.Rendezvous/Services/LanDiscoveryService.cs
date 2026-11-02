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
    private UdpClient? _listener;
    private Task? _listenerTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        logger.LogInformation("LAN pairing discovery listening on UDP port {Port}", Port);
        _listenerTask = ListenAsync(_listener, _stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping?.Cancel();
        try
        {
            if (_listenerTask is not null)
            {
                await _listenerTask.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (_stopping?.IsCancellationRequested == true)
        {
        }
        finally
        {
            _listener?.Dispose();
        }

        logger.LogInformation("LAN pairing discovery stopped.");
    }

    public void Dispose()
    {
        _stopping?.Cancel();
        _listener?.Dispose();
        _stopping?.Dispose();
    }

    private async Task ListenAsync(UdpClient listener, CancellationToken stoppingToken)
    {
        var requestBytes = Encoding.ASCII.GetBytes(DiscoveryRequest);
        var responseBytes = Encoding.ASCII.GetBytes(DiscoveryResponse);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var datagram = await listener.ReceiveAsync(stoppingToken);
                if (datagram.Buffer.AsSpan().SequenceEqual(requestBytes))
                {
                    await listener.SendAsync(responseBytes, datagram.RemoteEndPoint, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "LAN pairing discovery listener failed.");
        }
    }
}
