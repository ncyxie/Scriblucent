using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scriblucent.AI.Protocol;

// ── Host → Worker requests ──────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkerStartRequest), "start")]
[JsonDerivedType(typeof(WorkerGenerateRequest), "generate")]
[JsonDerivedType(typeof(WorkerStopRequest), "stop")]
[JsonDerivedType(typeof(WorkerShutdownRequest), "shutdown")]
[JsonDerivedType(typeof(WorkerTokenizeRequest), "tokenize")]
public abstract record WorkerRequest;

public sealed record WorkerStartRequest : WorkerRequest
{
    [JsonPropertyName("modelPath")]
    public string ModelPath { get; init; } = "";

    [JsonPropertyName("contextSize")]
    public int ContextSize { get; init; } = 4096;
}

public sealed record WorkerGenerateRequest : WorkerRequest
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; } = 1200;

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.7;

    [JsonPropertyName("topP")]
    public double TopP { get; init; } = 0.8;

    [JsonPropertyName("topK")]
    public int TopK { get; init; } = 20;

    [JsonPropertyName("presencePenalty")]
    public double PresencePenalty { get; init; } = 0.0;

    [JsonPropertyName("repeatPenalty")]
    public double RepeatPenalty { get; init; } = 1.0;

    [JsonPropertyName("grammar")]
    public string? Grammar { get; init; }

    [JsonPropertyName("grammarRoot")]
    public string? GrammarRoot { get; init; }
}

/// Unloads the model but leaves the worker process running, waiting for a new connection on the
/// same pipe name - used for idle-unload, where the whole point is to avoid a full process respawn.
public sealed record WorkerStopRequest : WorkerRequest;

/// Unloads the model and makes the worker process actually exit. Used for real teardown
/// (LLamaSharpWorkerAdapter.Dispose()), where WorkerStopRequest would leave the process orphaned
/// waiting for a connection that will never come.
public sealed record WorkerShutdownRequest : WorkerRequest;

public sealed record WorkerTokenizeRequest : WorkerRequest
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = "";
}

// ── Worker → Host responses ─────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkerStartedResponse), "started")]
[JsonDerivedType(typeof(WorkerTokenResponse), "token")]
[JsonDerivedType(typeof(WorkerGenerateCompleteResponse), "complete")]
[JsonDerivedType(typeof(WorkerErrorResponse), "error")]
[JsonDerivedType(typeof(WorkerTokenizeResponse), "tokenized")]
public abstract record WorkerResponse;

public sealed record WorkerStartedResponse : WorkerResponse
{
    [JsonPropertyName("modelKey")]
    public string ModelKey { get; init; } = "";
}

public sealed record WorkerTokenResponse : WorkerResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("generatedChars")]
    public int GeneratedChars { get; init; }
}

public sealed record WorkerGenerateCompleteResponse : WorkerResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("output")]
    public string Output { get; init; } = "";

    [JsonPropertyName("generatedChars")]
    public int GeneratedChars { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }
}

public sealed record WorkerErrorResponse : WorkerResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";
}

public sealed record WorkerTokenizeResponse : WorkerResponse
{
    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; init; }
}

// ── Wire protocol helpers ───────────────────────────────────────────────

public static class WorkerWire
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string SerializeResponse(WorkerResponse response)
        => JsonSerializer.Serialize(response, s_options);

    public static string SerializeRequest(WorkerRequest request)
        => JsonSerializer.Serialize(request, s_options);

    public static WorkerRequest? DeserializeRequest(string json)
        => JsonSerializer.Deserialize<WorkerRequest>(json, s_options);

    public static WorkerResponse? DeserializeResponse(string json)
        => JsonSerializer.Deserialize<WorkerResponse>(json, s_options);

    public static async Task WriteResponseAsync(Stream stream, WorkerResponse response, CancellationToken ct = default)
    {
        var json = SerializeResponse(response) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteRequestAsync(Stream stream, WorkerRequest request, CancellationToken ct = default)
    {
        var json = SerializeRequest(request) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}

/// <summary>
/// Stateful newline-delimited-JSON reader for one pipe connection. Named pipes opened in
/// PipeTransmissionMode.Byte give no message-boundary guarantee - a single ReadAsync can
/// legitimately return two or more already-written messages concatenated together (this happens
/// routinely during token streaming, where the worker can write faster than the host reads).
/// A stateless "read once, take the first line, discard the rest" reader silently drops any
/// message that arrives coalesced with another - this class instead buffers unconsumed bytes
/// across calls so nothing is ever lost. Scanning for the newline at the byte level (rather than
/// decoding each raw read chunk independently) also avoids corrupting a multi-byte UTF-8
/// character that happens to straddle two reads, since '\n' (0x0A) can never appear as part of a
/// multi-byte UTF-8 sequence.
/// </summary>
public sealed class WorkerLineReader
{
    private readonly Stream _stream;
    private byte[] _buffer = new byte[4096];
    private int _length;

    public WorkerLineReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<WorkerRequest?> ReadRequestAsync(CancellationToken ct = default)
    {
        var line = await ReadLineAsync(ct);
        return line is null ? null : WorkerWire.DeserializeRequest(line);
    }

    public async Task<WorkerResponse?> ReadResponseAsync(CancellationToken ct = default)
    {
        var line = await ReadLineAsync(ct);
        return line is null ? null : WorkerWire.DeserializeResponse(line);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var newlineIndex = Array.IndexOf(_buffer, (byte)'\n', 0, _length);
            if (newlineIndex >= 0)
            {
                var line = Encoding.UTF8.GetString(_buffer, 0, newlineIndex).Trim();

                var remaining = _length - (newlineIndex + 1);
                Buffer.BlockCopy(_buffer, newlineIndex + 1, _buffer, 0, remaining);
                _length = remaining;

                if (line.Length > 0)
                    return line;

                continue; // blank line - keep looking, matches the previous reader's behavior
            }

            if (_length == _buffer.Length)
                Array.Resize(ref _buffer, _buffer.Length * 2);

            var bytesRead = await _stream.ReadAsync(_buffer.AsMemory(_length, _buffer.Length - _length), ct);
            if (bytesRead == 0)
                return null;

            _length += bytesRead;
        }
    }
}
