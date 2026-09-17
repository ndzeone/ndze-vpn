using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NdzeVpn.Services;

public sealed record PresenceState(
    int ActivityType,
    string Details,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndsAt,
    string LargeImage,
    string LargeText,
    string SmallImage,
    string SmallText,
    IReadOnlyList<(string Label, string Url)> Buttons);

/// <summary>
/// Discord Rich Presence over Discord's local IPC pipe (<c>\\.\pipe\discord-ipc-N</c>).
/// Implemented directly — the protocol is four opcodes and a JSON body — so there is no
/// dependency to keep in sync with Discord client changes.
/// </summary>
public sealed class DiscordRpcService : IDisposable
{
    private enum Opcode { Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4 }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private string _clientId = "";
    private PresenceState? _pending;
    private CancellationTokenSource? _readerCts;
    private DateTime _nextReconnect = DateTime.MinValue;

    public bool IsConnected => _pipe is { IsConnected: true };

    public string? ConnectedUser { get; private set; }

    public event EventHandler<bool>? ConnectionChanged;

    public async Task SetPresenceAsync(string clientId, PresenceState presence)
    {
        _pending = presence;

        await _gate.WaitAsync();
        try
        {
            if (!await EnsureConnectedAsync(clientId)) return;
            await SendActivityAsync(presence);
        }
        catch (Exception ex)
        {
            LogBus.Instance.Debug("discord", $"SetActivity failed: {ex.Message}");
            DropConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearPresenceAsync()
    {
        _pending = null;

        await _gate.WaitAsync();
        try
        {
            if (!IsConnected) return;
            await SendFrameAsync(Opcode.Frame, new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = null },
                ["nonce"] = Guid.NewGuid().ToString()
            });
        }
        catch (Exception ex)
        {
            LogBus.Instance.Debug("discord", $"Clear failed: {ex.Message}");
            DropConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Disconnect()
    {
        _gate.Wait();
        try { DropConnection(); }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ connection

    private async Task<bool> EnsureConnectedAsync(string clientId)
    {
        if (IsConnected && clientId == _clientId) return true;
        if (IsConnected) DropConnection(); // application id changed in settings

        // Discord not running is the common case; do not hammer the pipe every tick.
        if (DateTime.UtcNow < _nextReconnect) return false;

        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(250);
            }
            catch
            {
                await pipe.DisposeAsync();
                continue;
            }

            _pipe = pipe;
            _clientId = clientId;

            try
            {
                await SendFrameAsync(Opcode.Handshake, new JsonObject
                {
                    ["v"] = 1,
                    ["client_id"] = clientId
                });

                var (op, body) = await ReadFrameAsync(CancellationToken.None);
                if (op != Opcode.Frame || body is null || body["evt"]?.GetValue<string>() != "READY")
                {
                    LogBus.Instance.Warn("discord", $"Handshake rejected: {body?.ToJsonString()}");
                    DropConnection();
                    _nextReconnect = DateTime.UtcNow.AddSeconds(30);
                    return false;
                }

                ConnectedUser = body["data"]?["user"]?["username"]?.GetValue<string>();
                LogBus.Instance.Info("discord", $"Connected to Discord{(ConnectedUser is null ? "" : $" as {ConnectedUser}")}");

                StartReader();
                ConnectionChanged?.Invoke(this, true);
                return true;
            }
            catch (Exception ex)
            {
                LogBus.Instance.Debug("discord", $"Handshake on pipe {i} failed: {ex.Message}");
                DropConnection();
            }
        }

        _nextReconnect = DateTime.UtcNow.AddSeconds(15);
        return false;
    }

    private void DropConnection()
    {
        var wasConnected = IsConnected;

        try { _readerCts?.Cancel(); } catch { }
        _readerCts?.Dispose();
        _readerCts = null;

        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        ConnectedUser = null;

        if (wasConnected)
        {
            LogBus.Instance.Info("discord", "Disconnected from Discord");
            ConnectionChanged?.Invoke(this, false);
        }
    }

    /// <summary>Drain inbound frames so the pipe buffer never fills, and answer pings.</summary>
    private void StartReader()
    {
        _readerCts = new CancellationTokenSource();
        var token = _readerCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    var (op, body) = await ReadFrameAsync(token);
                    var evt = body is null ? null : body["evt"]?.GetValue<string>();

                    if (op == Opcode.Ping)
                    {
                        await SendFrameAsync(Opcode.Pong, body ?? new JsonObject());
                    }
                    else if (op == Opcode.Close)
                    {
                        LogBus.Instance.Warn("discord", "Discord closed the connection: " + body?.ToJsonString());
                        DropConnection();
                        return;
                    }
                    else if (op == Opcode.Frame && evt == "ERROR")
                    {
                        LogBus.Instance.Warn("discord", "RPC error: " + body!["data"]?.ToJsonString());
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogBus.Instance.Debug("discord", $"Reader stopped: {ex.Message}");
                DropConnection();
            }
        }, token);
    }

    // ------------------------------------------------------------------ protocol

    private Task SendActivityAsync(PresenceState p)
    {
        var activity = new JsonObject
        {
            ["type"] = p.ActivityType,
            ["details"] = Clamp(p.Details),
            ["state"] = Clamp(p.State),
            ["instance"] = false
        };

        if (p.StartedAt is { } started)
        {
            // start + end is what makes Discord draw the "01:40 ━━━━━─ 02:12" bar.
            var timestamps = new JsonObject { ["start"] = started.ToUnixTimeMilliseconds() };
            if (p.EndsAt is { } ends) timestamps["end"] = ends.ToUnixTimeMilliseconds();
            activity["timestamps"] = timestamps;
        }

        var assets = new JsonObject();
        if (!string.IsNullOrWhiteSpace(p.LargeImage)) assets["large_image"] = p.LargeImage;
        if (!string.IsNullOrWhiteSpace(p.LargeText)) assets["large_text"] = Clamp(p.LargeText);
        if (!string.IsNullOrWhiteSpace(p.SmallImage)) assets["small_image"] = p.SmallImage;
        if (!string.IsNullOrWhiteSpace(p.SmallText)) assets["small_text"] = Clamp(p.SmallText);
        if (assets.Count > 0) activity["assets"] = assets;

        var buttons = new JsonArray();
        foreach (var (label, url) in p.Buttons.Take(2))
        {
            if (string.IsNullOrWhiteSpace(label) || !Uri.IsWellFormedUriString(url, UriKind.Absolute)) continue;
            buttons.Add(new JsonObject { ["label"] = Clamp(label, 32), ["url"] = url });
        }
        if (buttons.Count > 0) activity["buttons"] = buttons;

        return SendFrameAsync(Opcode.Frame, new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["activity"] = activity
            },
            ["nonce"] = Guid.NewGuid().ToString()
        });
    }

    /// <summary>Discord rejects the whole activity if a string is under 2 or over 128 chars.</summary>
    private static string Clamp(string value, int max = 128)
    {
        value = (value ?? "").Trim();
        if (value.Length < 2) value = value.PadRight(2, '⠀');
        return value.Length > max ? value[..(max - 1)] + "…" : value;
    }

    private async Task SendFrameAsync(Opcode op, JsonNode payload)
    {
        var pipe = _pipe ?? throw new IOException("Not connected");

        var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), (int)op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), json.Length);
        json.CopyTo(frame, 8);

        await pipe.WriteAsync(frame);
        await pipe.FlushAsync();
    }

    private async Task<(Opcode Op, JsonNode? Body)> ReadFrameAsync(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new IOException("Not connected");

        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);

        var op = (Opcode)BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 0 or > 1024 * 1024) throw new InvalidDataException($"Bad frame length {length}");

        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);

        return (op, length == 0 ? null : JsonNode.Parse(body));
    }

    public void Dispose()
    {
        DropConnection();
        _gate.Dispose();
    }
}
