using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace CubaseCompleteMcp.Core;

public static class PipeProtocol
{
    public const int MaximumMessageBytes = 8 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Pipe message exceeds {MaximumMessageBytes} bytes.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var headerRead = await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!headerRead)
        {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Invalid pipe message length: {length}.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("Pipe message deserialized to null.");
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Pipe closed in the middle of a message header.");
            }

            total += read;
        }

        return true;
    }
}
