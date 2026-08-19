using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Builder;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Envoy;

public sealed class WebServer : IAsyncDisposable
{
    public const int Port = 53821;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private WebApplication? _app;

    public string Address { get; private set; } = string.Empty;

    public static string? CurrentAddress { get; private set; }

    public async Task StartAsync()
    {
        if (_app is not null)
        {
            return;
        }

        var wirelessAddress = FindWirelessAddress();
        Address = $"http://{wirelessAddress}:{Port}";

        var builder = WebApplication.CreateBuilder();
        builder.Environment.WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        // Bind only to the active Wi-Fi IPv4 address, never to every network adapter.
        builder.WebHost.UseUrls(Address);
        builder.Services.AddSingleton<StorageService>();
        builder.Services.AddSingleton<PresenceService>();
        builder.Services.AddSingleton<MessageBus>();
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.UseWebSockets();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapHub<ChatHub>("/hub");
        app.Map("/ws", async (HttpContext context, MessageBus events) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await events.ListenWebSocketAsync(socket, context.RequestAborted);
        });
        app.MapGet("/api/messages", (StorageService store) => store.History());
        app.MapGet("/api/status", (StorageService store, PresenceService presence) =>
        {
            return Results.Ok(new
            {
                address = Address,
                storageBytes = store.StorageBytes,
                clients = presence.Count
            });
        });
        app.MapGet("/api/events", async (HttpResponse response, MessageBus events, CancellationToken cancel) =>
        {
            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers.Append("X-Accel-Buffering", "no");

            await foreach (var message in events.Listen(cancel))
            {
                // Use the same camelCase JSON contract as the REST endpoints.
                var payload = JsonSerializer.Serialize(message, WebJson);
                await response.WriteAsync($"data: {payload}\n\n", cancel);
                await response.Body.FlushAsync(cancel);
            }
        });
        app.MapPost("/api/messages", async (
            SendText body,
            StorageService store,
            IHubContext<ChatHub> hub,
            MessageBus events) =>
        {
            var message = await store.AddTextAsync(body.Sender, body.Text);
            await hub.Clients.All.SendAsync("message", message);
            await events.PublishAsync(message);
            return Results.Ok(message);
        });
        app.MapPost("/api/uploads", async (UploadRequest request, StorageService store) =>
        {
            return Results.Ok(await store.CreateUploadAsync(request));
        });
        app.MapGet("/api/uploads/{id:guid}", (Guid id, StorageService store) =>
        {
            var state = store.GetUpload(id);
            return Results.Ok(new
            {
                id,
                state.ChunkSize,
                state.ChunkCount,
                missingChunks = store.MissingChunks(id)
            });
        });
        app.MapPut("/api/uploads/{id:guid}/chunks/{index:int}", async (
            Guid id,
            int index,
            HttpRequest request,
            StorageService store) =>
        {
            if (!request.ContentLength.HasValue)
            {
                return Results.BadRequest("Missing Content-Length.");
            }

            var complete = await store.WriteChunkAsync(id, index, request.Body, request.ContentLength.Value);
            return Results.Ok(new { complete });
        });
        app.MapPost("/api/uploads/{id:guid}/complete", async (
            Guid id,
            StorageService store,
            IHubContext<ChatHub> hub,
            MessageBus events) =>
        {
            var message = await store.CompleteUploadAsync(id);
            await hub.Clients.All.SendAsync("message", message);
            await events.PublishAsync(message);
            return Results.Ok(message);
        });
        app.MapGet("/api/files/{id:guid}", (Guid id, StorageService store) =>
        {
            var message = store.History().FirstOrDefault(x =>
                x.File?.UploadId == id && x.File.ExpiresAt > DateTimeOffset.UtcNow);

            if (message?.File is not { } attachment || !File.Exists(store.FilePath(id)))
            {
                return Results.NotFound();
            }

            return Results.File(
                store.FilePath(id),
                attachment.ContentType,
                attachment.Name,
                enableRangeProcessing: true);
        });
        app.MapPost("/api/cleanup", async (StorageService store) =>
        {
            await store.CleanupAsync();
            return Results.Ok(new { storageBytes = store.StorageBytes });
        });
        app.MapFallbackToFile("index.html");

        await app.StartAsync();
        _app = app;
        CurrentAddress = Address;
    }

    public async Task StopAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        CurrentAddress = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static IPAddress FindWirelessAddress()
    {
        var wirelessInterface = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                networkInterface.OperationalStatus == OperationalStatus.Up)
            .Select(networkInterface => new
            {
                Network = networkInterface,
                Properties = networkInterface.GetIPProperties()
            })
            .Where(item => item.Properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.Any.Equals(gateway.Address)))
            .SelectMany(item => item.Properties.UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !IPAddress.Any.Equals(address));

        return wirelessInterface ?? throw new InvalidOperationException(
            "No active Wi-Fi IPv4 adapter was found. Connect to Wi-Fi and start Envoy again.");
    }
}

public sealed record SendText(string Sender, string Text);

public sealed class MessageBus
{
    private readonly List<System.Threading.Channels.Channel<ChatMessage>> _listeners = [];
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _webSockets = [];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(ChatMessage message)
    {
        lock (_lock)
        {
            foreach (var listener in _listeners)
            {
                listener.Writer.TryWrite(message);
            }
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, WebJson));
        foreach (var socket in _webSockets.Values)
        {
            if (socket.State != WebSocketState.Open)
            {
                continue;
            }

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // The receive loop will remove closed sockets.
            }
        }
    }

    public async IAsyncEnumerable<ChatMessage> Listen(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatMessage>();

        lock (_lock)
        {
            _listeners.Add(channel);
        }

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellation))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_lock)
            {
                _listeners.Remove(channel);
            }
        }
    }

    public async Task ListenWebSocketAsync(WebSocket socket, CancellationToken cancellation)
    {
        var id = Guid.NewGuid();
        _webSockets[id] = socket;
        var buffer = new byte[1024];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellation);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }
            }
        }
        catch (WebSocketException)
        {
            // A browser closing a tab normally ends the socket this way.
        }
        finally
        {
            _webSockets.TryRemove(id, out _);
        }
    }
}
