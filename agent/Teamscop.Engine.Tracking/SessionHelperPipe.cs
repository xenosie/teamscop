using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Teamscop.Engine.Sync;

namespace Teamscop.Engine.Tracking;

public static class SessionHelperPipeNames
{
    public const string Default = "Teamscop.Staff.Capture";
}

public sealed class SessionHelperMessage
{
    public string Type { get; set; } = "ping";
    public string? EventType { get; set; }
    public string? VaultKind { get; set; }
    public DateTimeOffset? OccurredAtUtc { get; set; }
    public string? PayloadBase64 { get; set; }
}

public sealed class SessionHelperReply
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>Length-prefixed JSON framing over a named pipe.</summary>
public static class SessionHelperPipeCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
        {
            return default;
        }

        var len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len <= 0 || len > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Invalid pipe frame length.");
        }

        var body = new byte[len];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            offset += n;
        }

        return true;
    }
}

/// <summary>Service-side pipe listener that accepts SessionHelper capture frames.</summary>
public sealed class SessionHelperPipeServer : IAsyncDisposable
{
    private readonly TrackingCoordinator _tracking;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SessionHelperPipeServer(TrackingCoordinator tracking, string? pipeName = null)
    {
        _tracking = tracking;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? SessionHelperPipeNames.Default : pipeName;
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleClientAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private NamedPipeServerStream CreateServer()
        => new(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && server.IsConnected)
        {
            var msg = await SessionHelperPipeCodec.ReadAsync<SessionHelperMessage>(server, ct).ConfigureAwait(false);
            if (msg is null)
            {
                break;
            }

            try
            {
                if (string.Equals(msg.Type, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    _tracking.NoteSessionHelperAlive();
                    await SessionHelperPipeCodec.WriteAsync(server, new SessionHelperReply { Ok = true }, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(msg.Type, "capture", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(msg.EventType)
                        || string.IsNullOrWhiteSpace(msg.VaultKind)
                        || string.IsNullOrWhiteSpace(msg.PayloadBase64))
                    {
                        await SessionHelperPipeCodec.WriteAsync(server,
                            new SessionHelperReply { Ok = false, Error = "capture fields missing" }, ct)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var plain = Convert.FromBase64String(msg.PayloadBase64);
                    var when = msg.OccurredAtUtc ?? DateTimeOffset.UtcNow;
                    await _tracking.PersistFromHelperAsync(msg.EventType, msg.VaultKind, plain, when, ct)
                        .ConfigureAwait(false);
                    await SessionHelperPipeCodec.WriteAsync(server, new SessionHelperReply { Ok = true }, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                await SessionHelperPipeCodec.WriteAsync(server,
                    new SessionHelperReply { Ok = false, Error = "unknown type" }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    await SessionHelperPipeCodec.WriteAsync(server,
                        new SessionHelperReply { Ok = false, Error = ex.Message }, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}

/// <summary>Client used by Teamscop.SessionHelper.</summary>
public sealed class SessionHelperPipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _stream;

    public SessionHelperPipeClient(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? SessionHelperPipeNames.Default : pipeName;
    }

    public bool IsConnected => _stream is { IsConnected: true };

    public async Task EnsureConnectedAsync(CancellationToken ct, int timeoutMs = 5000)
    {
        if (IsConnected)
        {
            return;
        }

        _stream?.Dispose();
        _stream = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await _stream.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
    }

    public async Task PingAsync(CancellationToken ct)
    {
        EnsureConnected();
        await SessionHelperPipeCodec.WriteAsync(_stream!, new SessionHelperMessage { Type = "ping" }, ct)
            .ConfigureAwait(false);
        var reply = await SessionHelperPipeCodec.ReadAsync<SessionHelperReply>(_stream!, ct).ConfigureAwait(false);
        if (reply is not { Ok: true })
        {
            throw new InvalidOperationException(reply?.Error ?? "ping failed");
        }
    }

    public async Task SendCaptureAsync(
        string eventType,
        string vaultKind,
        byte[] plain,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct)
    {
        EnsureConnected();
        await SessionHelperPipeCodec.WriteAsync(_stream!, new SessionHelperMessage
        {
            Type = "capture",
            EventType = eventType,
            VaultKind = vaultKind,
            OccurredAtUtc = occurredAtUtc,
            PayloadBase64 = Convert.ToBase64String(plain)
        }, ct).ConfigureAwait(false);
        var reply = await SessionHelperPipeCodec.ReadAsync<SessionHelperReply>(_stream!, ct).ConfigureAwait(false);
        if (reply is not { Ok: true })
        {
            throw new InvalidOperationException(reply?.Error ?? "capture failed");
        }
    }

    private void EnsureConnected()
    {
        if (_stream is null || !_stream.IsConnected)
        {
            throw new InvalidOperationException("Pipe not connected.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}
