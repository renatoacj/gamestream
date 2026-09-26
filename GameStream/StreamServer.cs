using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace GameStream;

/// <summary>
/// Servidor HTTP embutido (só em localhost; o acesso externo chega pelo túnel).
/// Serve as páginas, a API de controle usada pela janela do app e distribui a
/// transmissão para cada espectador por WebSocket.
/// </summary>
public sealed class StreamServer : IAsyncDisposable
{
    // Protocolo binário (servidor -> espectador):
    //   [0] tipo: 1 = vídeo, 2 = áudio
    //   [1] flags: bit 0 = quadro-chave
    //   [2..9] timestamp em µs (float64 little-endian)
    //   [10..] dados codificados (H.264 Annex B ou Opus)
    public const byte KindVideo = 1;
    public const byte KindAudio = 2;
    private const int HeaderSize = 10;

    /// <summary>Se a fila de um espectador passar disso, a conexão dele está lenta: descartamos e recomeçamos num quadro-chave.</summary>
    private const long ViewerBacklogLimit = 4 * 1024 * 1024;

    private readonly int _port;
    private readonly string _webRoot;
    private WebApplication? _app;

    private readonly ConcurrentDictionary<string, Viewer> _viewers = new();
    private volatile string? _config;   // último "config" (codecs), reenviado para quem entra

    /// <summary>Token secreto usado só pela janela do app.</summary>
    public string HostToken { get; } = NewToken(24);

    /// <summary>Chave que vai no link compartilhado com os espectadores. Pode ser trocada (<see cref="RotateViewKey"/>).</summary>
    public string ViewKey { get; private set; } = NewToken(12);

    /// <summary>Limite de espectadores simultâneos: protege seu upload se o link vazar.</summary>
    public int MaxViewers { get; set; } = 10;

    public int Port => _port;

    /// <summary>Túnel que gera o link público; o servidor só lê o endereço e o status.</summary>
    public PublicTunnel? Tunnel { get; set; }

    public Broadcaster? Broadcaster { get; set; }

    public int ViewerCount => _viewers.Values.Count(v => !v.IsPreview);

    public StreamServer(int port, string webRoot)
    {
        _port = port;
        _webRoot = webRoot;
    }

    /// <summary>Tenta a porta preferida e as seguintes, caso já estejam em uso.</summary>
    public static async Task<StreamServer> StartOnFreePortAsync(int firstPort, string webRoot, int attempts = 20)
    {
        for (var port = firstPort; ; port++)
        {
            var server = new StreamServer(port, webRoot);
            try
            {
                await server.StartAsync();
                return server;
            }
            catch (Exception ex) when (ex is IOException or SocketException && port < firstPort + attempts - 1)
            {
                // Porta ocupada ou reservada pelo Windows (Hyper-V/WSL): tenta a próxima.
            }
        }
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = _webRoot,
        });
        builder.Logging.ClearProviders();
        // Só localhost: o acesso externo chega pelo túnel (PublicTunnel), sem abrir portas no Windows.
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(_port));

        var app = builder.Build();

        // Cabeçalhos de segurança em todas as respostas: a página só carrega o que vem do
        // próprio app (sem scripts de terceiros), não pode ser embutida em outros sites e
        // não vaza o endereço (com a chave) para outros sites.
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
                "connect-src 'self' ws: wss:; frame-ancestors 'self'; base-uri 'none'; form-action 'none'; object-src 'none'";
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next();
        });

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        var files = new PhysicalFileProvider(_webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, DefaultFileNames = { "index.html" } });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-store",
        });

        MapHostApi(app);
        app.Map("/ws", HandleWebSocket);

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        _app = app;
    }

    // ---------- API da janela do app ----------

    private void MapHostApi(WebApplication app)
    {
        app.MapGet("/api/status", (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            var b = Broadcaster;
            var (fps, kbps) = b?.SampleStats() ?? (0, 0);
            var publicUrl = Tunnel?.Url;
            return Results.Json(new
            {
                state = b?.State ?? "offline",
                message = b?.Message,
                encoder = b?.Encoder,
                outputSize = b?.OutputSize,
                source = b?.SourceName,
                audio = b?.AudioLabel,
                fps = Math.Round(fps),
                kbps = Math.Round(kbps),
                viewers = ViewerCount,
                maxViewers = MaxViewers,
                publicUrl = publicUrl == null ? null : $"{publicUrl}/?k={ViewKey}",
                tunnelStatus = Tunnel?.Status ?? "Desativado",
                previewUrl = $"/?preview=1&t={HostToken}",
            });
        });

        app.MapGet("/api/sources", (HttpContext ctx) =>
            IsHost(ctx) ? Results.Json(CaptureSources.List()) : Results.Unauthorized());

        app.MapGet("/api/level", (HttpContext ctx) =>
            IsHost(ctx) ? Results.Json(new { db = Broadcaster?.TakeAudioLevelDb() }) : Results.Unauthorized());

        app.MapGet("/api/thumb", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            var jpeg = await Thumbnails.GetAsync(ctx.Request.Query["id"].ToString());
            return jpeg == null ? Results.NotFound() : Results.File(jpeg, "image/jpeg");
        });

        app.MapPost("/api/start", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx) || Broadcaster == null) return Results.Unauthorized();
            var settings = await ctx.Request.ReadFromJsonAsync<StreamSettings>();
            if (settings == null) return Results.BadRequest();
            _ = Broadcaster.StartAsync(settings);   // o progresso aparece em /api/status
            return Results.Ok();
        });

        app.MapPost("/api/stop", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx) || Broadcaster == null) return Results.Unauthorized();
            await Broadcaster.StopAsync();
            return Results.Ok();
        });

        app.MapPost("/api/rotate-key", (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            RotateViewKey();
            return Results.Ok();
        });

        app.MapPost("/api/max-viewers", (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            if (!int.TryParse(ctx.Request.Query["n"], out var n)) return Results.BadRequest();
            MaxViewers = Math.Clamp(n, 1, 50);
            return Results.Ok();
        });
    }

    // Pelo túnel as requisições também chegam de 127.0.0.1, por isso o token é obrigatório.
    private bool IsHost(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) &&
        KeyMatches(ctx.Request.Query["t"], HostToken);

    /// <summary>Comparação em tempo constante (não revela quantos caracteres acertou).</summary>
    private static bool KeyMatches(string? provided, string expected) =>
        provided != null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    /// <summary>Gera um link novo e desconecta todos os espectadores atuais (ex.: o link vazou).</summary>
    public void RotateViewKey()
    {
        ViewKey = NewToken(12);
        foreach (var v in _viewers.Values.Where(v => !v.IsPreview))
        {
            v.EnqueueText("""{"type":"expired"}""");
            _ = Task.Delay(500).ContinueWith(_ => v.Abort());
        }
    }

    // ---------- Distribuição da mídia ----------

    public static byte[] BuildPacket(byte kind, bool key, long timestampMicros, byte[] data)
    {
        var packet = new byte[HeaderSize + data.Length];
        packet[0] = kind;
        packet[1] = (byte)(key ? 1 : 0);
        BitConverter.TryWriteBytes(packet.AsSpan(2, 8), (double)timestampMicros);
        Buffer.BlockCopy(data, 0, packet, HeaderSize, data.Length);
        return packet;
    }

    public void PublishConfig(string config)
    {
        _config = config;
        foreach (var v in _viewers.Values) v.SendConfig(config);
    }

    public void PublishOffline()
    {
        if (_config == null) return;
        _config = null;
        foreach (var v in _viewers.Values) v.EnqueueText("""{"type":"offline"}""");
    }

    public void PublishMedia(byte[] packet, bool isVideo, bool isKey)
    {
        foreach (var v in _viewers.Values) v.OfferMedia(packet, isVideo, isKey);
    }

    private async Task HandleWebSocket(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var role = ctx.Request.Query["role"].ToString();
        // A prévia (dentro da janela do app) não conta no limite, então exige o token do app.
        var allowed = role switch
        {
            "viewer" => KeyMatches(ctx.Request.Query["k"], ViewKey),
            "preview" => IsHost(ctx),
            _ => false,
        };
        if (!allowed)
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        var viewer = new Viewer(NewToken(6), await ctx.WebSockets.AcceptWebSocketAsync(), isPreview: role == "preview");
        if (!viewer.IsPreview && ViewerCount >= MaxViewers)
        {
            viewer.EnqueueText("""{"type":"full"}""");
            await viewer.RunSendLoopAsync(closeWhenEmpty: true);
            return;
        }
        _viewers[viewer.Id] = viewer;
        _ = viewer.RunSendLoopAsync();

        var config = _config;
        viewer.EnqueueText($$"""{"type":"hello","live":{{(config != null ? "true" : "false")}}}""");
        if (config != null) viewer.SendConfig(config);

        // O espectador não manda nada; só esperamos a conexão fechar.
        await viewer.WaitForCloseAsync();

        _viewers.TryRemove(viewer.Id, out _);
        viewer.Abort();
    }

    private static string NewToken(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
        }
    }

    /// <summary>Espectador: WebSocket com fila de envio própria, para que um cliente lento não trave os outros.</summary>
    private sealed class Viewer(string id, WebSocket socket, bool isPreview)
    {
        private readonly record struct Item(byte[] Data, bool IsText, long Generation);

        private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
        private long _queuedBytes;
        private long _generation;

        // Um decodificador só consegue começar num quadro-chave.
        private volatile bool _waitingForKey = true;

        public string Id { get; } = id;
        public bool IsPreview { get; } = isPreview;

        public void EnqueueText(string text) => Enqueue(Encoding.UTF8.GetBytes(text), true, droppable: false);

        public void SendConfig(string config)
        {
            _waitingForKey = true;
            EnqueueText(config);
        }

        public void OfferMedia(byte[] packet, bool isVideo, bool isKey)
        {
            if (_waitingForKey)
            {
                if (!isVideo || !isKey) return;
                _waitingForKey = false;
            }

            if (Interlocked.Read(ref _queuedBytes) > ViewerBacklogLimit)
            {
                // Internet do espectador não está dando conta: descarta o atraso e
                // recomeça no próximo quadro-chave (no máximo 1 s depois).
                Interlocked.Increment(ref _generation);
                _waitingForKey = true;
                return;
            }

            Enqueue(packet, isText: false, droppable: true);
        }

        private void Enqueue(byte[] data, bool isText, bool droppable)
        {
            Interlocked.Add(ref _queuedBytes, data.Length);
            _queue.Writer.TryWrite(new Item(data, isText, droppable ? Interlocked.Read(ref _generation) : -1));
        }

        /// <param name="closeWhenEmpty">Envia o que já está na fila e fecha a conexão (ex.: aviso de "sala cheia").</param>
        public async Task RunSendLoopAsync(bool closeWhenEmpty = false)
        {
            if (closeWhenEmpty) _queue.Writer.TryComplete();
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync())
                {
                    Interlocked.Add(ref _queuedBytes, -item.Data.Length);
                    if (item.Generation >= 0 && item.Generation != Interlocked.Read(ref _generation)) continue;
                    await socket.SendAsync(item.Data,
                        item.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                        true, CancellationToken.None);
                }
                if (closeWhenEmpty)
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "full", CancellationToken.None);
            }
            catch
            {
                Abort();
            }
        }

        public async Task WaitForCloseAsync()
        {
            var buffer = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var r = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
        }

        public void Abort()
        {
            _queue.Writer.TryComplete();
            try { socket.Abort(); } catch { }
        }
    }
}
