using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

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
                    if (IsLocalAddress(datagram.RemoteEndPoint.Address))
                    {
                        logger.LogDebug(
                            "Ignored LAN discovery request originating from this PC ({RemoteEndPoint}).",
                            datagram.RemoteEndPoint);
                        continue;
                    }

                    var sent = await listener.SendAsync(responseBytes, datagram.RemoteEndPoint, stoppingToken);
                    logger.LogInformation(
                        "Received LAN discovery request from {RemoteEndPoint} ({RequestBytes} bytes); sent {ResponseBytes} response bytes",
                        datagram.RemoteEndPoint,
                        datagram.Buffer.Length,
                        sent);
                }
                else
                {
                    logger.LogDebug(
                        "Ignored unrecognized LAN discovery datagram from {RemoteEndPoint} ({Length} bytes)",
                        datagram.RemoteEndPoint,
                        datagram.Buffer.Length);
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

    private static bool IsLocalAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));
}
