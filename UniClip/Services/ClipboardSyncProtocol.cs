using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace UniClip.Services;

public sealed class ClipboardSyncProtocol : ISessionProtocol<string, bool>
{
    public const int MaximumTextBytes = ClipboardSyncLimits.MaximumTextBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public const string ProtocolId = "/unclip/clipboard-sync/1.0.0";
    public string Id => ProtocolId;
    public Func<string, CancellationToken, Task<bool>>? IncomingClipboardHandler { get; set; }

    public Task<bool> SendAsync(ISession session, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(text);

        var byteCount = StrictUtf8.GetByteCount(text);
        if (byteCount > MaximumTextBytes)
        {
            throw new InvalidOperationException($"Clipboard text exceeds the {MaximumTextBytes / 1024} KiB sync limit.");
        }

        return session.DialAsync<ClipboardSyncProtocol, string, bool>(text, cancellationToken);
    }

    public async Task<bool> DialAsync(IChannel downChannel, ISessionContext context, string request)
    {
        var cancellationToken = context.UpgradeOptions?.CancellationToken ?? downChannel.CancellationToken;
        var payload = StrictUtf8.GetBytes(request);
        if (payload.Length > MaximumTextBytes)
        {
            throw new InvalidDataException("Clipboard text exceeds the sync limit.");
        }

        await WriteAsync(downChannel, payload, cancellationToken);
        var accepted = await downChannel.ReadVarintAsync(cancellationToken);
        return accepted switch
        {
            1 => true,
            0 => false,
            _ => throw new InvalidDataException("The receiving device sent an invalid clipboard response.")
        };
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        var cancellationToken = context.UpgradeOptions?.CancellationToken ?? downChannel.CancellationToken;
        try
        {
            var length = await downChannel.ReadVarintAsync(cancellationToken);
            if (length < 0 || length > MaximumTextBytes)
            {
                throw new InvalidDataException("Clipboard text exceeds the sync limit.");
            }

            byte[] payload;
            if (length == 0)
            {
                payload = [];
            }
            else
            {
                var read = await downChannel.ReadAsync(length, ReadBlockingMode.WaitAll, cancellationToken);
                if (read.Result != IOResult.Ok || read.Data.Length != length)
                {
                    throw new EndOfStreamException("The sender disconnected while sending clipboard text.");
                }

                payload = read.Data.ToArray();
            }

            var text = StrictUtf8.GetString(payload);
            var handler = IncomingClipboardHandler;
            var accepted = handler is not null && await handler(text, cancellationToken);
            await downChannel.WriteVarintAsync(accepted ? 1 : 0);
        }
        catch (DecoderFallbackException)
        {
            await downChannel.WriteVarintAsync(0);
        }
        catch (InvalidDataException)
        {
            await downChannel.WriteVarintAsync(0);
        }
    }

    private static async Task WriteAsync(IChannel channel, byte[] payload, CancellationToken cancellationToken)
    {
        await channel.WriteVarintAsync(payload.Length);
        if (payload.Length == 0)
        {
            return;
        }

        var result = await channel.WriteAsync(new ReadOnlySequence<byte>(payload), cancellationToken);
        if (result != IOResult.Ok)
        {
            throw new IOException($"Clipboard transfer failed: {result}.");
        }
    }
}
