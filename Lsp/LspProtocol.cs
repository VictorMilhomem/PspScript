using System.Text;
using System.Text.Json;

namespace PspScript.Lsp;

/// <summary>
/// Minimal zero-dependency JSON-RPC 2.0 transport implementing the LSP
/// <c>Content-Length: N\r\n\r\n</c> framing protocol over any <see cref="Stream"/>.
/// </summary>
internal sealed class LspTransport
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public LspTransport(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task<JsonDocument?> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        int contentLength = -1;
        var headerLine = new List<byte>(64);

        while (true)
        {
            headerLine.Clear();
            int prev = -1;
            while (true)
            {
                int b = ReadByteSyncOrAsync();
                if (b < 0)
                    return null;

                if (prev == '\r' && b == '\n')
                {
                    // Remove trailing '\r'
                    if (headerLine.Count > 0)
                        headerLine.RemoveAt(headerLine.Count - 1);
                    break;
                }

                headerLine.Add((byte)b);
                prev = b;
            }

            if (headerLine.Count == 0)
            {
                // Blank line marks end of headers
                break;
            }

            string line = Encoding.ASCII.GetString(headerLine.ToArray());
            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line[prefix.Length..].Trim(), out int len))
                {
                    contentLength = len;
                }
            }
        }

        if (contentLength <= 0)
            return null;

        byte[] payload = new byte[contentLength];
        int readTotal = 0;
        while (readTotal < contentLength)
        {
            int n = await _input.ReadAsync(payload.AsMemory(readTotal, contentLength - readTotal), cancellationToken);
            if (n == 0)
                return null;
            readTotal += n;
        }

        return JsonDocument.Parse(payload);
    }

    private int ReadByteSyncOrAsync()
    {
        return _input.ReadByte();
    }

    public async Task SendResponseAsync(JsonElement id, object? result, CancellationToken cancellationToken = default)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
        await WritePayloadAsync(envelope, cancellationToken);
    }

    public async Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params
        };
        await WritePayloadAsync(envelope, cancellationToken);
    }

    private async Task WritePayloadAsync(object envelope, CancellationToken cancellationToken)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        byte[] headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {jsonBytes.Length}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(headerBytes, cancellationToken);
            await _output.WriteAsync(jsonBytes, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
