using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MajdataEdit_NeoX.Base;
using MajProtocol.Generated;
using MajProtocol.Standard;
using MajProtocol.Standard.Packets;

namespace MajdataEdit_NeoX.Models.Connection;

internal sealed class PlayerConnection : IMajProtocolClient, IAsyncDisposable
{
    private sealed class Endpoint : IAsyncDisposable
    {
        public Endpoint(string ip)
        {
            Ip = ip;
            Outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public          string                  Ip { get; }
        public          ClientWebSocket?        Client;
        public readonly Channel<byte[]>         Outgoing;
        public readonly SemaphoreSlim           ConnectGate = new(1, 1);
        public readonly CancellationTokenSource LifetimeCts = new();
        public          Task?                   SendLoop;
        public          Task?                   ReceiveLoop;
        public          bool                    Disposed;

        public bool IsConnected => Client?.State == WebSocketState.Open;

        public async Task<bool> ConnectAsync(PlayerConnection owner)
        {
            if (IsConnected) return true;

            await ConnectGate.WaitAsync(LifetimeCts.Token);
            try
            {
                if (IsConnected) return true;

                var client = new ClientWebSocket();
                client.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                await client.ConnectAsync(new Uri(MajWsProtocol.CreateServerUrl(Ip)), LifetimeCts.Token);

                var previous = Interlocked.Exchange(ref Client, client);
                if (previous is not null)
                {
                    try
                    {
                        await previous.CloseAsync(WebSocketCloseStatus.NormalClosure, "rotate", CancellationToken.None);
                    }
                    catch
                    {
                        /* swallow */
                    }

                    previous.Dispose();
                }

                SendLoop    = Task.Run(() => SendLoopAsync(this, LifetimeCts.Token));
                ReceiveLoop = Task.Run(() => owner.ReceiveLoopAsync(this, LifetimeCts.Token));
                owner.OnConnectedChanged?.Invoke(true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                ConnectGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Disposed) return;
            Disposed = true;
            LifetimeCts.Cancel();
            Outgoing.Writer.TryComplete();

            var client = Interlocked.Exchange(ref Client, null);
            if (client is not null)
            {
                try
                {
                    if (client.State == WebSocketState.Open)
                        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
                }
                catch
                {
                    /* swallow */
                }

                client.Dispose();
            }

            if (SendLoop is not null)
            {
                try
                {
                    await SendLoop;
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    /* swallow */
                }
            }

            if (ReceiveLoop is not null)
            {
                try
                {
                    await ReceiveLoop;
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    /* swallow */
                }
            }

            LifetimeCts.Dispose();
            ConnectGate.Dispose();
        }
    }

    private readonly List<Endpoint> _endpoints = new();
    private          bool           _disposed;

    public PlayerConnection(IEnumerable<string> ips)
    {
        SetEndpoints(ips);
    }

    public bool IsConnected => ActiveConnectionCount > 0;

    public int ActiveConnectionCount { get; private set; }

    public event Action<bool>? OnConnectedChanged;

    public async Task<bool> ConnectAsync()
    {
        if (_disposed) return false;
        if (_endpoints.Count == 0) return false;

        var results = await Task.WhenAll(_endpoints.Select(async endpoint =>
        {
            var connected = await endpoint.ConnectAsync(this);
            if (connected)
                await endpoint.Outgoing.Writer.WriteAsync(
                    new HandshakeRequest { protocolVersion = Properties.ProtocolVersion }
                );

            return connected;
        }));
        UpdateActiveConnectionCount();
        return results.Any(result => result);
    }

    public async Task SetPlayersAsync(IEnumerable<string> ips)
    {
        if (_disposed) return;

        var normalized   = NormalizeIps(ips);
        var oldEndpoints = _endpoints.ToArray();

        _endpoints.Clear();
        _endpoints.AddRange(normalized.Select(ip => new Endpoint(ip)));
        ActiveConnectionCount = 0;

        await Task.WhenAll(oldEndpoints.Select(endpoint => endpoint.DisposeAsync().AsTask()));
        UpdateActiveConnectionCount();
        if (!_disposed) await ConnectAsync();
    }

    private static string[] NormalizeIps(IEnumerable<string> ips)
        => ips
           .Where(ip => !string.IsNullOrWhiteSpace(ip))
           .Select(ip => ip.Trim())
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .ToArray();

    private void SetEndpoints(IEnumerable<string> ips)
    {
        _endpoints.AddRange(NormalizeIps(ips).Select(ip => new Endpoint(ip)));
    }

    public async Task BroadcastAsync(byte[] bytes)
    {
        await Task.WhenAll(_endpoints.Where(endpoint => endpoint.IsConnected).Select(endpoint =>
        {
            try
            {
                return endpoint.LifetimeCts.IsCancellationRequested
                    ? Task.CompletedTask
                    : endpoint.Outgoing.Writer.WriteAsync(bytes).AsTask();
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }));
    }

    private static async Task SendLoopAsync(Endpoint endpoint, CancellationToken ct)
    {
        var client = endpoint.Client;
        if (client is null) return;
        try
        {
            await foreach (var packet in endpoint.Outgoing.Reader.ReadAllAsync(ct))
                await client.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlayerConnection] send {endpoint.Ip}: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(Endpoint endpoint, CancellationToken ct)
    {
        var client = endpoint.Client;
        if (client is null) return;
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                using var              ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var span = ms.ToArray().AsSpan();
                (this as IMajProtocolClient).Handle(span);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlayerConnection] receive {endpoint.Ip}: {ex.Message}");
        }
        finally
        {
            UpdateActiveConnectionCount();
        }
    }

    private void UpdateActiveConnectionCount()
    {
        var count = _endpoints.Count(endpoint => endpoint.IsConnected);
        if (count == ActiveConnectionCount) return;
        ActiveConnectionCount = count;
        OnConnectedChanged?.Invoke(count > 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _endpoints.Clear();
        ActiveConnectionCount = 0;

        await Task.WhenAll(_endpoints.Select(endpoint => endpoint.DisposeAsync().AsTask()));
        OnConnectedChanged?.Invoke(false);
    }

    public event HandshakeResponse.Callback? OnHandshake;
    public event FilesResponse.Callback?     OnFiles;
    public event SimaiResponse.Callback?     OnSimai;
    public event PlayResponse.Callback?      OnPlay;
    public event PauseResponse.Callback?     OnPause;
    public event OptionsResponse.Callback?   OnOptions;
    public event StateResponse.Callback?     OnState;
    public event UnloadResponse.Callback?    OnUnload;

    public void OnFilesResponseReceived(FilesResponse req)
    {
        OnFiles?.Invoke(req);
    }

    public void OnHandshakeResponseReceived(HandshakeResponse req)
    {
        OnHandshake?.Invoke(req);
    }

    public void OnOptionsResponseReceived(OptionsResponse req)
    {
        OnOptions?.Invoke(req);
    }

    public void OnPauseResponseReceived(PauseResponse req)
    {
        OnPause?.Invoke(req);
    }

    public void OnPlayResponseReceived(PlayResponse req)
    {
        OnPlay?.Invoke(req);
    }

    public void OnSimaiResponseReceived(SimaiResponse req)
    {
        OnSimai?.Invoke(req);
    }

    public void OnStateResponseReceived(StateResponse req)
    {
        OnState?.Invoke(req);
    }

    public void OnUnloadResponseReceived(UnloadResponse req)
    {
        OnUnload?.Invoke(req);
    }
}
