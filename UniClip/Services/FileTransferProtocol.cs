using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace UniClip.Services;

public sealed record FileTransferProgress(string FileName, long BytesTransferred, long TotalBytes, bool IsIncoming);
public sealed record IncomingFileRequest(string PeerId, string FileName, long Length);

public sealed class FileTransferProtocol(string receiveDirectory) : ISessionProtocol<string, string>
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumHeaderLength = 2048;
    private const int MaximumFileNameBytes = 240;
    private readonly ConcurrentDictionary<string, PendingFile> _pendingFiles = new(StringComparer.Ordinal);

    public const string ProtocolId = "/unclip/file-transfer/1.0.0";
    public string Id => ProtocolId;
    public Func<IncomingFileRequest, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }
    public event Action<FileTransferProgress>? ProgressChanged;
    public event Action<string>? FileReceived;

    public async Task<string> SendAsync(
        ISession session,
        Stream source,
        string fileName,
        long length,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(source);
        ValidateFileName(fileName);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var transferId = Guid.NewGuid().ToString("N");
        var pendingFile = new PendingFile(source, fileName, length);
        if (!_pendingFiles.TryAdd(transferId, pendingFile))
        {
            throw new InvalidOperationException("Could not register the outgoing file transfer.");
        }

        try
        {
            return await session.DialAsync<FileTransferProtocol, string, string>(transferId, cancellationToken);
        }
        finally
        {
            _pendingFiles.TryRemove(transferId, out _);
        }
    }

    public async Task<string> DialAsync(IChannel downChannel, ISessionContext context, string request)
    {
        if (!_pendingFiles.TryGetValue(request, out var pendingFile))
        {
            throw new InvalidOperationException("The requested outgoing transfer is no longer available.");
        }

        var header = new TransferHeader(pendingFile.FileName, pendingFile.Length);
        var headerJson = JsonSerializer.Serialize(header);
        if (Encoding.UTF8.GetByteCount(headerJson) > MaximumHeaderLength)
        {
            throw new InvalidOperationException("The file transfer metadata exceeds the protocol limit.");
        }

        var cancellationToken = context.UpgradeOptions?.CancellationToken ?? downChannel.CancellationToken;
        await WriteLineAsync(downChannel, headerJson, cancellationToken);
        var response = await ReadControlAsync(downChannel, cancellationToken);
        if (!response.Accepted)
        {
            throw new IOException(response.Message ?? "The receiving device rejected this file.");
        }

        using var channelStream = new ChannelStream(downChannel, leaveOpen: true);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long transferred = 0;
        while (transferred < pendingFile.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesToRead = (int)Math.Min(buffer.Length, pendingFile.Length - transferred);
            var bytesRead = await pendingFile.Source.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("The selected file ended before its reported size.");
            }

            hasher.AppendData(buffer.AsSpan(0, bytesRead));
            await channelStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            transferred += bytesRead;
            ProgressChanged?.Invoke(new FileTransferProgress(pendingFile.FileName, transferred, pendingFile.Length, IsIncoming: false));
        }

        var completed = await ReadControlAsync(downChannel, cancellationToken);
        var expectedHash = Convert.ToHexString(hasher.GetHashAndReset());
        if (!completed.Accepted)
        {
            throw new IOException(completed.Message ?? "The receiving device could not save this file.");
        }

        if (!string.Equals(completed.Sha256, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The received file did not match the SHA-256 digest calculated by the sender.");
        }

        return completed.Message ?? "File received and verified.";
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        var cancellationToken = context.UpgradeOptions?.CancellationToken ?? downChannel.CancellationToken;
        TransferHeader validatedHeader;
        try
        {
            var headerJson = await ReadLineAsync(downChannel, MaximumHeaderLength, cancellationToken);
            var header = JsonSerializer.Deserialize<TransferHeader>(headerJson);
            if (header is null || header.Length < 0)
            {
                await WriteControlAsync(downChannel, TransferControl.Reject("Invalid file metadata."), cancellationToken);
                return;
            }

            validatedHeader = header;
            ValidateFileName(validatedHeader.FileName);
            var remotePeerId = context.State.RemotePeerId
                ?? throw new InvalidDataException("The remote peer identity is not available.");
            var approvalHandler = ApprovalHandler;
            if (approvalHandler is null ||
                !await approvalHandler(
                    new IncomingFileRequest(remotePeerId.ToString(), validatedHeader.FileName, validatedHeader.Length),
                    cancellationToken))
            {
                await WriteControlAsync(downChannel, TransferControl.Reject("The receiving device declined this file."), cancellationToken);
                return;
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            await WriteControlAsync(downChannel, TransferControl.Reject("Invalid file metadata."), cancellationToken);
            return;
        }

        string partialPath;
        FileStream destination;
        try
        {
            Directory.CreateDirectory(receiveDirectory);
            partialPath = Path.Combine(receiveDirectory, $".unclip-{Guid.NewGuid():N}.partial");
            destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await WriteControlAsync(downChannel, TransferControl.Reject($"Could not prepare the receive folder: {exception.Message}"), cancellationToken);
            return;
        }

        await using (destination)
        {
            try
            {
                await WriteControlAsync(downChannel, TransferControl.Accept(), cancellationToken);
                using var channelStream = new ChannelStream(downChannel, leaveOpen: true);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[BufferSize];
                long transferred = 0;
                while (transferred < validatedHeader.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytesToRead = (int)Math.Min(buffer.Length, validatedHeader.Length - transferred);
                    var bytesRead = await channelStream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
                    if (bytesRead == 0)
                    {
                        throw new EndOfStreamException("The sender disconnected before the file was complete.");
                    }

                    hasher.AppendData(buffer.AsSpan(0, bytesRead));
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    transferred += bytesRead;
                    ProgressChanged?.Invoke(new FileTransferProgress(validatedHeader.FileName, transferred, validatedHeader.Length, IsIncoming: true));
                }

                await destination.FlushAsync(cancellationToken);
                var hash = Convert.ToHexString(hasher.GetHashAndReset());
                var completedPath = MoveToUniqueDestination(partialPath, validatedHeader.FileName);
                await WriteControlAsync(
                    downChannel,
                    new TransferControl(true, $"Received {Path.GetFileName(completedPath)}", hash),
                    cancellationToken);
                FileReceived?.Invoke(completedPath);
            }
            catch
            {
                destination.Close();
                File.Delete(partialPath);
                throw;
            }
        }
    }

    private static async Task<TransferControl> ReadControlAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var controlJson = await ReadLineAsync(channel, MaximumHeaderLength, cancellationToken);
        return JsonSerializer.Deserialize<TransferControl>(controlJson)
            ?? throw new InvalidDataException("The other device sent an invalid file-transfer response.");
    }

    private static async Task WriteControlAsync(IChannel channel, TransferControl control, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(control);
        await WriteLineAsync(channel, json, cancellationToken);
    }

    private static async Task<string> ReadLineAsync(IChannel channel, int maximumLength, CancellationToken cancellationToken)
    {
        var length = await channel.ReadVarintAsync(cancellationToken);
        if (length < 1 || length > maximumLength)
        {
            throw new InvalidDataException($"The file-transfer header length {length} is invalid.");
        }

        var result = await channel.ReadAsync(length, ReadBlockingMode.WaitAll, cancellationToken);
        if (result.Result != IOResult.Ok)
        {
            throw new EndOfStreamException("The other device disconnected while sending file-transfer metadata.");
        }

        return Encoding.UTF8.GetString(result.Data.ToArray()).TrimEnd('\n');
    }

    private static async Task WriteLineAsync(IChannel channel, string value, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes(value + "\n");
        var prefix = new byte[VarInt.GetSizeInBytes(data.Length)];
        var prefixLength = 0;
        VarInt.Encode(data.Length, prefix, ref prefixLength);

        var prefixResult = await channel.WriteAsync(new ReadOnlySequence<byte>(prefix), cancellationToken);
        if (prefixResult != IOResult.Ok)
        {
            throw new IOException($"Could not write file-transfer metadata length: {prefixResult}.");
        }

        var dataResult = await channel.WriteAsync(new ReadOnlySequence<byte>(data), cancellationToken);
        if (dataResult != IOResult.Ok)
        {
            throw new IOException($"Could not write file-transfer metadata: {dataResult}.");
        }
    }

    private static string MoveToUniqueDestination(string partialPath, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var index = 0; index < 1000; index++)
        {
            var suffix = index == 0 ? string.Empty : $" ({index})";
            var candidate = Path.Combine(Path.GetDirectoryName(partialPath)!, $"{baseName}{suffix}{extension}");
            try
            {
                File.Move(partialPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }

        throw new IOException("Could not find a unique name for the received file.");
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            Encoding.UTF8.GetByteCount(fileName) > MaximumFileNameBytes ||
            Path.IsPathRooted(fileName) ||
            fileName is "." or ".." ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException("The file name is invalid.", nameof(fileName));
        }

        foreach (var character in fileName)
        {
            if (char.IsControl(character) || "<>:\"|?*".Contains(character))
            {
                throw new ArgumentException("The file name is invalid.", nameof(fileName));
            }
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName).TrimEnd(' ', '.');
        var reservedName = baseName.Split('.')[0];
        if (reservedName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            reservedName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            reservedName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            reservedName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            IsReservedNumberedName(reservedName, "COM") ||
            IsReservedNumberedName(reservedName, "LPT"))
        {
            throw new ArgumentException("The file name is reserved on a supported platform.", nameof(fileName));
        }
    }

    private static bool IsReservedNumberedName(string value, string prefix) =>
        value.Length == prefix.Length + 1 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[^1] is >= '1' and <= '9';

    private sealed record PendingFile(Stream Source, string FileName, long Length);
    private sealed record TransferHeader(string FileName, long Length);
    private sealed record TransferControl(bool Accepted, string? Message, string? Sha256)
    {
        public static TransferControl Accept() => new(true, null, null);
        public static TransferControl Reject(string message) => new(false, message, null);
    }
}
