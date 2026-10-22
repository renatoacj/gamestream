using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace GameStream;

/// <summary>
/// Servidor HTTP embutido (só em localhost; o acesso externo chega pelo túnel).
/// Serve as páginas, a API de controle usada pela janela do app e mantém a "sala":
/// participantes, as telas compartilhadas (a do app e as dos convidados) e a
/// distribuição de cada transmissão para quem escolheu assistir.
/// </summary>
public sealed partial class StreamServer : IAsyncDisposable, IRoomSink
{
    // Protocolo binário (nos dois sentidos):
    //   [0]     tipo: 1 = vídeo, 2 = áudio, 3 = miniatura (JPEG)
    //   [1]     flags: bit 0 = quadro-chave
    //   [2..3]  número da transmissão (u16 LE; o servidor sobrescreve o que vem do convidado)
    //   [4..11] timestamp em µs (float64 LE)
    //   [12..]  dados (H.264 Annex B / VP8, Opus ou JPEG)
    public const byte KindVideo = 1;
    public const byte KindAudio = 2;
    public const byte KindThumb = 3;
    private const int HeaderSize = 12;
    private const ushort HostStreamNo = 0;

    /// <summary>Se a fila de um participante passar disso, a conexão dele está lenta: descartamos e recomeçamos num quadro-chave.</summary>
    private const long BacklogLimit = 4 * 1024 * 1024;

    // Limites para o que um convidado pode enviar (ninguém do link é confiável por padrão).
    private const int MaxTextBytes = 8 * 1024;
    private const int MaxMediaBytes = 3 * 1024 * 1024;
    private const int MaxThumbBytes = 256 * 1024;
    private const long MaxPublishBytesPerSecond = 4 * 1024 * 1024;   // ~32 Mbps
    private const int MaxStreams = 8;

    private readonly int _port;
    private readonly string _webRoot;
    private WebApplication? _app;

    private readonly ConcurrentDictionary<string, Participant> _people = new();
    private readonly ConcurrentDictionary<ushort, RoomStream> _streams = new();
    private int _nextStreamNo;

    // Endereços de quem o anfitrião removeu da sala: não entram de novo enquanto o app estiver aberto.
    /// <summary>
    /// Endereços de quem foi removido → até quando (Environment.TickCount64). Dura 10 minutos, ou até o anfitrião
    /// gerar um código novo ou reabrir a sala (aí ele está chamando a pessoa de volta).
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _removedIps = new();
    private static readonly long RemovalMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    /// <summary>Token secreto usado só pela janela do app.</summary>
    public string HostToken { get; } = NewToken(24);

    /// <summary>Chave que vai no link compartilhado. Pode ser trocada (<see cref="RotateViewKey"/>).</summary>
    public string ViewKey { get; private set; } = NewToken(12);

    /// <summary>Limite de pessoas na sala; 0 = sem limite.</summary>
    public int MaxViewers { get; set; }

    /// <summary>
    /// Se quem entra também pode compartilhar a própria tela. Desligado = "transmissão" (só o app transmite);
    /// ligado = "sala" (todos podem compartilhar). O app abre em transmissão.
    /// </summary>
    public bool AllowGuestShare { get; private set; }

    public int Port => _port;

    /// <summary>Túnel que gera o link público; o servidor só lê o endereço e o status.</summary>
    public PublicTunnel? Tunnel { get; set; }

    public Broadcaster? Broadcaster { get; set; }

    /// <summary>
    /// Senha curta da sala (6 números), usada no "código da sala". Vale junto com a chave longa do link
    /// e é trocada com ela. Como é curta, tentativas erradas demais bloqueiam a entrada por código por um tempo.
    /// </summary>
    public string RoomPin { get; private set; } = NewPin();

    /// <summary>
    /// Código da sala: as palavras do endereço do túnel + a senha curta (ex.: "spears-manhattan-calgary-until-482913").
    /// É o próprio endereço da sala em formato mais fácil de passar; não depende de nenhum serviço.
    /// </summary>
    public string? RoomCode =>
        AllowGuestShare && CurrentRoomGuid is { } guid ? RoomIds.Code(RoomName, guid)   // sala salva: código fixo
        : Tunnel?.Url is { } url && RoomClient.TunnelName(url) is { } name ? $"{name}-{RoomPin}" : null;

    /// <summary>GUID da sala salva aberta agora (código fixo), ou null na transmissão.</summary>
    public string? CurrentRoomGuid { get; private set; }

    /// <summary>Chaves das salas salvas deste PC: quem chega com uma delas enquanto a sala está fechada espera.</summary>
    private string[] _savedRoomKeys = [];

    /// <summary>Publica o endereço atual das salas salvas, para o código fixo continuar valendo.</summary>
    private readonly RoomDirectory _directory;

    /// <summary>Toca no próprio app o som da tela escolhida na sala (ver <see cref="RoomAudioPlayer"/>).</summary>
    public RoomAudioPlayer Audio { get; } = new();

    private const int MaxWrongPins = 30;                        // tentativas erradas toleradas…
    private static readonly TimeSpan PinWindow = TimeSpan.FromMinutes(5);   // …neste intervalo, antes de bloquear
    private readonly object _pinLock = new();
    private int _wrongPins;
    private DateTime _pinWindowStart = DateTime.UtcNow;
    private DateTime _pinBlockedUntil;

    private enum KeyCheck { Wrong, Ok, Closed }

    /// <summary>
    /// Chaves de salas fechadas nesta execução do app (cada sala salva tem a sua). Quem chega com uma delas
    /// ouve "a sala está fechada" em vez de "código errado", e a página fica esperando a sala reabrir.
    /// </summary>
    private readonly List<(string Key, string Pin)> _closedKeys = [];
    private const int MaxClosedKeys = 32;

    /// <summary>Confere a chave de quem quer entrar: a chave longa do link ou a senha curta do código.</summary>
    private bool ViewerKeyMatches(string? provided) => CheckViewerKey(provided) == KeyCheck.Ok;

    private KeyCheck CheckViewerKey(string? provided)
    {
        if (provided == null) return KeyCheck.Wrong;
        if (KeyMatches(provided, ViewKey)) return KeyCheck.Ok;
        var isPin = provided.Length == RoomPin.Length;

        lock (_pinLock)
        {
            var now = DateTime.UtcNow;
            if (isPin)
            {
                // Durante o bloqueio nenhuma senha curta é aceita, nem para dizer que a sala está fechada.
                if (now < _pinBlockedUntil) return KeyCheck.Wrong;
                if (now - _pinWindowStart > PinWindow)
                {
                    _pinWindowStart = now;
                    _wrongPins = 0;
                }
                if (KeyMatches(provided, RoomPin)) return KeyCheck.Ok;
            }
            foreach (var (key, pin) in _closedKeys)
            {
                if (KeyMatches(provided, key) || (isPin && KeyMatches(provided, pin))) return KeyCheck.Closed;
            }
            foreach (var key in _savedRoomKeys)
            {
                if (KeyMatches(provided, key)) return KeyCheck.Closed;
            }
            if (isPin && ++_wrongPins >= MaxWrongPins)
            {
                // Alguém está chutando: a entrada por código para por um tempo (o link continua valendo).
                _pinBlockedUntil = now + PinWindow;
                _wrongPins = 0;
                Tools.Log("Muitas tentativas erradas de código: entrada por código bloqueada por 5 minutos.");
            }
            return KeyCheck.Wrong;
        }
    }

    /// <summary>
    /// Troca a chave e a senha curta por outras já conhecidas (as da sala salva que está sendo aberta, ou as da
    /// transmissão). As anteriores ficam guardadas como "fechadas": quem estava conectado espera e volta sozinho
    /// quando elas forem usadas de novo.
    /// </summary>
    /// <param name="keepOld">false = a chave anterior deixa de valer de vez ("código novo"): quem estava sai.</param>
    private void SetViewKey(string key, string pin, bool keepOld = true)
    {
        if (key == ViewKey && pin == RoomPin) return;
        _removedIps.Clear();   // abriu outra sala (ou reabriu esta): começa sem ninguém bloqueado
        lock (_pinLock)
        {
            _closedKeys.RemoveAll(k => k.Key == ViewKey || k.Key == key);
            if (keepOld) _closedKeys.Add((ViewKey, RoomPin));
            if (_closedKeys.Count > MaxClosedKeys) _closedKeys.RemoveAt(0);
            ViewKey = key;
            RoomPin = pin;
        }
        var notice = keepOld ? """{"type":"closed"}""" : """{"type":"expired"}""";
        foreach (var p in _people.Values.Where(p => !p.IsHost))
        {
            p.EnqueueText(notice);
            _ = Task.Delay(500).ContinueWith(_ => p.Abort());
        }
    }

    private static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public int ViewerCount => _people.Values.Count(p => !p.IsHost && !IsHiddenCompanion(p));

    /// <summary>Sala de outra pessoa em que este app entrou (null = usando a própria sala).</summary>
    public RoomClient? Joined { get; private set; }

    /// <summary>
    /// A conexão que o app de um convidado usa para enviar a tela ("companheira") não aparece na
    /// lista de pessoas quando a página da sala do mesmo PC também está conectada: são uma pessoa só.
    /// Sozinha ela aparece normalmente, para ninguém ficar na sala sem ser visto.
    /// </summary>
    private bool IsHiddenCompanion(Participant p) =>
        p.IsCompanion && p.Device != "" && _people.Values.Any(o => !o.IsCompanion && o.Device == p.Device);

    public StreamServer(int port, string webRoot)
    {
        _port = port;
        _webRoot = webRoot;
        _directory = new RoomDirectory(() => Tunnel?.Url);
        _ = PingLoopAsync(_life.Token);
        UpdateChecker.Start(_life.Token);
    }

    private readonly CancellationTokenSource _life = new();
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private const long SilenceLimitMs = 45_000;

    /// <summary>
    /// Quem some sem fechar a conexão (internet caiu, notebook suspenso) continuaria na sala como "fantasma":
    /// o WebSocket não percebe sozinho. Um ping a cada 15 s; quem sabe responder e fica 45 s calado sai da sala.
    /// </summary>
    private async Task PingLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(PingInterval, token); } catch (OperationCanceledException) { break; }
            var now = Environment.TickCount64;
            foreach (var p in _people.Values)
            {
                if (p.AnswersPing && now - p.LastSeen > SilenceLimitMs)
                {
                    Tools.Log($"Sem resposta há {(now - p.LastSeen) / 1000} s: {p.Name} saiu da sala.");
                    p.Abort();
                    continue;
                }
                p.EnqueueText("""{"type":"ping"}""");
            }
        }
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
                "default-src 'self'; script-src 'self'; worker-src 'self'; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob:; media-src 'self' blob: mediastream:; connect-src 'self' ws: wss:; " +
                // frame-src: o app mostra a sala de outra pessoa (pelo túnel) dentro da própria janela.
                // frame-ancestors: e a nossa sala pode ser mostrada dentro do app de um convidado (localhost dele).
                "frame-src 'self' https://*.trycloudflare.com; frame-ancestors 'self' http://localhost:*; " +
                "base-uri 'none'; form-action 'none'; object-src 'none'";
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            // display-capture: os convidados podem compartilhar a tela; câmera e microfone continuam bloqueados.
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), display-capture=(self)";
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
        // O app de um convidado confere a chave antes de insistir na conexão: 204 = vale, 401 = código/link errado.
        // Versão do app desta sala (pública): quem entra pelo app avisa quando as versões são diferentes.
        app.MapGet("/api/version", () => Results.Json(new { version = AppVersion.Current }));

        // 204 = pode entrar; 423 = a sala existe mas está fechada (vai reabrir); 401 = chave errada.
        app.MapGet("/api/check", (HttpContext ctx) => CheckViewerKey(ctx.Request.Query["k"]) switch
        {
            KeyCheck.Ok => Results.NoContent(),
            KeyCheck.Closed => Results.StatusCode(StatusCodes.Status423Locked),
            _ => Results.Unauthorized(),
        });
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
                allowShare = AllowGuestShare,
                viewKey = ViewKey,
                pin = RoomPin,
                guestStreams = _streams.Values.Count(s => s.No != HostStreamNo),
                publicUrl = publicUrl == null ? null : $"{publicUrl}/?k={ViewKey}",
                tunnelStatus = Tunnel?.Status ?? "Desativado",
                code = RoomCode,
                roomUrl = $"/?embedded=1&t={HostToken}&ver={AppVersion.Current}",
                version = AppVersion.Current,
                update = AppSettings.Current.CheckUpdatesEnabled && UpdateChecker.NewVersion != null
                    ? new { version = UpdateChecker.NewVersion }
                    : null,
                joined = Joined == null ? null : new
                {
                    state = Joined.State,
                    message = Joined.Message,
                    reason = Joined.Reason,
                    hostVersion = Joined.HostVersion,
                    roomUrl = Joined.RoomPageUrl,
                    origin = Joined.Origin,
                    hostName = Joined.HostName,
                    roomName = Joined.RoomName,
                },
            });
        });

        // Entrar na sala de outra pessoa: a transmissão deste app passa a ir para lá.
        app.MapPost("/api/join", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx) || Broadcaster == null) return Results.Unauthorized();
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var link = body?["url"]?.GetValue<string>() ?? "";
            var name = CleanName(body?["name"]?.GetValue<string>(), isHost: false);

            if (RoomCode != null && string.Equals(link.Trim(), RoomCode, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "Esse é o código da sua própria sala." }, statusCode: 400);
            if (RoomIds.GuidFromCode(link) is { } ownGuid && _savedRoomKeys.Contains(RoomIds.ViewKey(ownGuid)))
                return Results.Json(new { error = "Esse é o código de uma sala sua: abra-a em Minhas salas." }, statusCode: 400);

            var client = RoomClient.TryCreate(link, name, out var error);
            if (client == null) return Results.Json(new { error }, statusCode: 400);

            await LeaveJoinedRoomAsync();
            client.Audio = Audio;
            client.PublishRejected += reason => _ = Broadcaster.StopWithMessageAsync(reason);
            Joined = client;
            await Broadcaster.SetSinkAsync(client);
            return Results.Ok();
        });

        app.MapPost("/api/leave", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx) || Broadcaster == null) return Results.Unauthorized();
            await LeaveJoinedRoomAsync();
            return Results.Ok();
        });

        app.MapGet("/api/sources", (HttpContext ctx) =>
            IsHost(ctx) ? Results.Json(CaptureSources.List()) : Results.Unauthorized());

        app.MapGet("/api/level", (HttpContext ctx) =>
            IsHost(ctx)
                ? Results.Json(new { db = Broadcaster?.TakeAudioLevelDb(), autoGainDb = Broadcaster?.AutoGainDb })
                : Results.Unauthorized());

        app.MapGet("/api/thumb", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            var jpeg = await Thumbnails.GetAsync(ctx.Request.Query["id"].ToString());
            return jpeg == null ? Results.NotFound() : Results.File(jpeg, "image/jpeg");
        });

        // Quais codificadores este PC tem (a interface marca os indisponíveis).
        app.MapGet("/api/encoders", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            try
            {
                var available = await VideoCapture.ProbeEncodersAsync(await Tools.EnsureFfmpegAsync());
                return Results.Json(VideoCapture.EncoderList.Select(e => new { id = e.Id, label = e.Label, available = available.GetValueOrDefault(e.Id) }));
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/start", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx) || Broadcaster == null) return Results.Unauthorized();
            var settings = await ctx.Request.ReadFromJsonAsync<StreamSettings>();
            if (settings == null) return Results.BadRequest();
            // switch=1: trocar a janela/monitor sem encerrar a transmissão para quem assiste.
            var switching = ctx.Request.Query["switch"] == "1" && Broadcaster.State == "live";
            _ = Broadcaster.StartAsync(settings, switching);   // o progresso aparece em /api/status
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

        // As salas salvas deste PC (GUIDs): o endereço delas é publicado, e quem chega enquanto estão fechadas espera.
        app.MapPost("/api/rooms", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            var guids = (body?["rooms"] as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>()
                .Where(RoomIds.IsGuid).Distinct().Take(50).ToArray() ?? [];
            _savedRoomKeys = guids.Select(RoomIds.ViewKey).ToArray();
            _directory.SetRooms(guids);
            return Results.Ok();
        });

        // A página da sala (dentro do app) avisa qual tela toca som; o app toca (ver RoomAudioPlayer).
        app.MapPost("/api/listen", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            ushort? stream = body?["stream"] is JsonValue sv && sv.TryGetValue<int>(out var n) && n is >= 0 and <= ushort.MaxValue ? (ushort)n : null;
            var volume = body?["volume"] is JsonValue vv && vv.TryGetValue<double>(out var v) ? (float)v : 1f;
            var muted = body?["muted"] is JsonValue mv && mv.TryGetValue<bool>(out var m) && m;
            Joined?.Listen(stream);
            Audio.Listen(stream, volume, muted);
            return Results.Ok();
        });

        app.MapPost("/api/max-viewers", (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            if (!int.TryParse(ctx.Request.Query["n"], out var n)) return Results.BadRequest();
            MaxViewers = Math.Clamp(n, 0, 500);
            return Results.Ok();
        });

        app.MapPost("/api/allow-share", (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            // Opcional: a chave da sala salva que está sendo aberta (ou a da transmissão, ao voltar para ela).
            // Sala salva: room=<guid> (a chave sai do GUID, e o código fica fixo); fresh=1 = código novo.
            var key = ctx.Request.Query["key"].ToString();
            var pin = ctx.Request.Query["pin"].ToString();
            var room = ctx.Request.Query["room"].ToString();
            if (room != "")
            {
                if (!RoomIds.IsGuid(room)) return Results.BadRequest();
                SetViewKey(RoomIds.ViewKey(room), RoomIds.Pin(room), keepOld: ctx.Request.Query["fresh"] != "1");
                CurrentRoomGuid = room;
            }
            else if (key != "" || pin != "")
            {
                if (!ViewKeyPattern().IsMatch(key) || !PinPattern().IsMatch(pin)) return Results.BadRequest();
                SetViewKey(key, pin);
                CurrentRoomGuid = null;
            }
            if (ctx.Request.Query["on"] != "1") CurrentRoomGuid = null;
            RoomName = CleanRoomName(ctx.Request.Query["name"]);
            SetAllowGuestShare(ctx.Request.Query["on"] == "1");
            return Results.Ok();
        });

        // Preferências do app: fechar para a bandeja e iniciar com o Windows.
        app.MapGet("/api/app-settings", (HttpContext ctx) =>
            IsHost(ctx)
                ? Results.Json(new
                {
                    closeToTray = AppSettings.Current.CloseToTray,
                    startWithWindows = AppSettings.StartWithWindows,
                    name = AppSettings.Current.Name,
                    fixedRoomCodes = AppSettings.Current.FixedRoomCodesEnabled,
                    checkUpdates = AppSettings.Current.CheckUpdatesEnabled,
                    defaultName = Environment.UserName,
                })
                : Results.Unauthorized());

        app.MapPost("/api/app-settings", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            if (body?["closeToTray"]?.GetValue<bool>() is { } tray)
            {
                AppSettings.Current.CloseToTray = tray;
                AppSettings.Current.Save();
            }
            if (body?["name"]?.GetValue<string>() is { } name)
            {
                var clean = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
                AppSettings.Current.Name = clean.Length > 24 ? clean[..24] : clean;
                AppSettings.Current.Save();
            }
            if (body?["fixedRoomCodes"]?.GetValue<bool>() is { } fixedCodes)
            {
                AppSettings.Current.FixedRoomCodes = fixedCodes;
                AppSettings.Current.Save();
            }
            if (body?["checkUpdates"]?.GetValue<bool>() is { } checkUpdates)
            {
                AppSettings.Current.CheckUpdates = checkUpdates;
                AppSettings.Current.Save();
                if (checkUpdates) _ = UpdateChecker.CheckAsync();
            }
            if (body?["startWithWindows"]?.GetValue<bool>() is { } autostart)
            {
                try { AppSettings.StartWithWindows = autostart; }
                catch (Exception ex) { return Results.Json(new { error = "Não foi possível mudar a inicialização: " + ex.Message }, statusCode: 500); }
            }
            return Results.Ok();
        });

        // Dados da interface guardados pelo app (ver AppSettings.Store).
        app.MapGet("/api/store", (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            lock (AppSettings.Current.Store) return Results.Json(new Dictionary<string, string>(AppSettings.Current.Store));
        });

        app.MapPost("/api/store", async (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>();
            if (body == null) return Results.BadRequest();
            var store = AppSettings.Current.Store;
            lock (store)
            {
                foreach (var (key, node) in body)
                {
                    if (!StoreKeyPattern().IsMatch(key)) continue;
                    if (node is null) { store.Remove(key); continue; }
                    if (node is not JsonValue v || !v.TryGetValue<string>(out var value) || value.Length > 256 * 1024) continue;
                    if (store.Count >= 40 && !store.ContainsKey(key)) continue;
                    if (key is "gamestream-my-rooms" or "gamestream-rooms" && store.TryGetValue(key, out var before))
                    {
                        var (was, now) = (CountItems(before), CountItems(value));
                        if (was >= 2 && now < was - 1) Tools.Log($"Aviso: a lista '{key}' passou de {was} para {now} itens de uma vez.");
                    }
                    store[key] = value;
                }
            }
            AppSettings.Current.Save();
            return Results.Ok();
        });

        app.MapPost("/api/open-privacy", (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            Process.Start(new ProcessStartInfo("https://github.com/renatoacj/gamestream/blob/main/PRIVACY.md") { UseShellExecute = true });
            return Results.Ok();
        });

        app.MapPost("/api/open-update", (HttpContext ctx) =>
            IsHost(ctx) && UpdateChecker.OpenDownloadPage() ? Results.Ok() : Results.BadRequest());

        app.MapPost("/api/open-log", (HttpContext ctx) =>
        {
            if (!IsHost(ctx)) return Results.Unauthorized();
            Tools.Log("Log aberto pelo usuário.");
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { Tools.LogPath }, UseShellExecute = false });
            return Results.Ok();
        });
    }

    private async Task LeaveJoinedRoomAsync()
    {
        if (Broadcaster != null) await Broadcaster.SetSinkAsync(this);
        Joined?.Dispose();
        Joined = null;
        Audio.Listen(null, 1, false);
    }

    // Pelo túnel as requisições também chegam de 127.0.0.1, por isso o token é obrigatório.
    private bool IsHost(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) &&
        KeyMatches(ctx.Request.Query["t"], HostToken);

    /// <summary>Comparação em tempo constante (não revela quantos caracteres acertou).</summary>
    private static bool KeyMatches(string? provided, string expected) =>
        provided != null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    /// <summary>Gera um link novo e desconecta todos os convidados (ex.: o link vazou).</summary>
    public void RotateViewKey()
    {
        _removedIps.Clear();   // código novo: quem foi removido pode voltar se receber o código
        ViewKey = NewToken(12);
        RoomPin = NewPin();
        foreach (var p in _people.Values.Where(p => !p.IsHost))
        {
            p.EnqueueText("""{"type":"expired"}""");
            _ = Task.Delay(500).ContinueWith(_ => p.Abort());
        }
    }

    private void SetAllowGuestShare(bool allow)
    {
        AllowGuestShare = allow;
        if (!allow)
        {
            foreach (var s in _streams.Values.Where(s => s.No != HostStreamNo))
                RemoveStream(s, "O anfitrião desativou o compartilhamento de tela dos convidados.");
        }
        BroadcastRoom();
    }

    // ---------- Transmissão do próprio app (stream 0) ----------

    public static byte[] BuildPacket(byte kind, bool key, long timestampMicros, byte[] data, ushort streamNo = HostStreamNo)
    {
        var packet = new byte[HeaderSize + data.Length];
        packet[0] = kind;
        packet[1] = (byte)(key ? 1 : 0);
        BitConverter.TryWriteBytes(packet.AsSpan(2, 2), streamNo);
        BitConverter.TryWriteBytes(packet.AsSpan(4, 8), (double)timestampMicros);
        Buffer.BlockCopy(data, 0, packet, HeaderSize, data.Length);
        return packet;
    }

    /// <summary>O app começou (ou mudou) a transmissão. <paramref name="config"/> = { video: {codec}, audio: {...}|null }.</summary>
    public void PublishConfig(JsonObject config)
    {
        var stream = _streams.GetOrAdd(HostStreamNo, _ => new RoomStream(HostStreamNo, ownerId: ""));
        stream.SetConfig(config);
        foreach (var p in _people.Values) p.SendConfigIfWatching(stream);
        BroadcastRoom();
    }

    public void PublishOffline()
    {
        if (_streams.TryGetValue(HostStreamNo, out var stream)) RemoveStream(stream, null);
    }

    public void PublishMedia(byte[] packet, bool isVideo, bool isKey)
    {
        if (_streams.TryGetValue(HostStreamNo, out var stream)) FanOut(stream, packet, isVideo, isKey);
    }

    public void PublishThumbnail(byte[] jpeg)
    {
        if (_streams.TryGetValue(HostStreamNo, out var stream)) SetThumbnail(stream, BuildPacket(KindThumb, false, 0, jpeg));
    }

    // ---------- Sala ----------

    private void FanOut(RoomStream stream, byte[] packet, bool isVideo, bool isKey)
    {
        var needKey = false;
        // O mesmo cadeado de StartWatching: quem começa a assistir recebe o trecho guardado
        // e, em seguida, exatamente os pacotes seguintes (sem buraco nem repetição).
        if (!isVideo && Joined == null) Audio.OnPacket(packet);   // som da minha sala, tocado pelo app
        lock (stream.Sync)
        {
            if (isVideo) stream.Remember(packet, isKey);
            foreach (var p in _people.Values)
            {
                if (p.Id != stream.OwnerId && p.OfferMedia(stream.No, packet, isVideo, isKey)) needKey = true;
            }
        }
        // Alguém entrou agora ou ficou para trás: pede um quadro-chave ao convidado que transmite.
        // (A transmissão do app já manda quadros-chave em intervalo fixo.)
        if (needKey && stream.OwnerId != "" && stream.ShouldRequestKeyframe() &&
            _people.TryGetValue(stream.OwnerId, out var owner))
        {
            owner.EnqueueText("""{"type":"keyframe"}""");
        }
    }

    private void SetThumbnail(RoomStream stream, byte[] packet)
    {
        stream.Thumb = packet;
        foreach (var p in _people.Values) p.EnqueueDroppable(packet);
    }

    private void RemoveStream(RoomStream stream, string? reasonForOwner)
    {
        if (!_streams.TryRemove(stream.No, out _)) return;
        foreach (var p in _people.Values)
        {
            p.StopWatching(stream.No);
            if (p.Id == stream.OwnerId)
            {
                p.Publishing = null;
                if (reasonForOwner != null)
                    p.EnqueueText(new JsonObject { ["type"] = "unpublished", ["reason"] = reasonForOwner }.ToJsonString());
            }
        }
        BroadcastRoom();
    }

    private string HostName => _people.Values.FirstOrDefault(p => p.IsHost)?.Name ?? "Anfitrião";

    private void BroadcastRoom()
    {
        var people = new JsonArray();
        foreach (var p in _people.Values.Where(p => !IsHiddenCompanion(p)).OrderBy(p => p.JoinedAt))
            people.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["host"] = p.IsHost, ["v"] = p.Version });

        var streams = new JsonArray();
        foreach (var s in _streams.Values.OrderBy(s => s.No))
        {
            _people.TryGetValue(s.OwnerId, out var owner);
            // Tela enviada pelo app de um convidado: usa o nome que ele escolheu na página da sala.
            var person = owner is { IsCompanion: true }
                ? _people.Values.FirstOrDefault(o => !o.IsCompanion && o.Device == owner.Device) ?? owner
                : owner;
            var name = s.OwnerId == "" ? HostName : person?.Name ?? "?";
            streams.Add(new JsonObject
            {
                ["no"] = s.No, ["owner"] = s.OwnerId, ["dev"] = owner?.Device ?? "", ["name"] = name, ["audio"] = s.HasAudio,
            });
        }

        var msg = new JsonObject
        {
            ["type"] = "room",
            ["people"] = people,
            ["streams"] = streams,
            ["canShare"] = AllowGuestShare,
            ["title"] = AllowGuestShare ? RoomName : "",
        }.ToJsonString();
        foreach (var p in _people.Values) p.EnqueueText(msg);
    }

    private async Task HandleWebSocket(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var role = ctx.Request.Query["role"].ToString();
        // "host" é a sala embutida na janela do app: não conta no limite e exige o token do app.
        var check = role switch
        {
            "viewer" => CheckViewerKey(ctx.Request.Query["k"]),
            "host" => IsHost(ctx) ? KeyCheck.Ok : KeyCheck.Wrong,
            _ => KeyCheck.Wrong,
        };
        if (check == KeyCheck.Closed)
        {
            // Sala fechada por enquanto: avisa e desconecta; a página tenta de novo até ela reabrir.
            var waiting = new Participant(NewToken(6), await ctx.WebSockets.AcceptWebSocketAsync(), isHost: false);
            waiting.EnqueueText("""{"type":"closed"}""");
            await waiting.RunSendLoopAsync(closeWhenEmpty: true);
            return;
        }
        if (check != KeyCheck.Ok)
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        // "dev" liga as conexões de um mesmo PC (app + página da sala); "pub" marca a conexão do app que envia a tela.
        var device = ctx.Request.Query["dev"].ToString();
        if (!DevicePattern().IsMatch(device)) device = "";
        var companion = role == "viewer" && device != "" && ctx.Request.Query["pub"] == "1";

        // Pelo túnel todo mundo chega como 127.0.0.1; o endereço real vem neste cabeçalho, posto pela Cloudflare.
        var ip = role == "viewer" ? ctx.Request.Headers["CF-Connecting-IP"].ToString() : "";

        var me = new Participant(NewToken(6), await ctx.WebSockets.AcceptWebSocketAsync(), isHost: role == "host")
        {
            Device = device,
            IsCompanion = companion,
            Ip = ip,
        };
        if (ip != "" && _removedIps.TryGetValue(ip, out var bannedUntil) && Environment.TickCount64 < bannedUntil)
        {
            me.EnqueueText("""{"type":"removed"}""");
            await me.RunSendLoopAsync(closeWhenEmpty: true);
            return;
        }
        if (!me.IsHost && MaxViewers > 0 && ViewerCount >= MaxViewers)
        {
            me.EnqueueText("""{"type":"full"}""");
            await me.RunSendLoopAsync(closeWhenEmpty: true);
            return;
        }

        if (device != "" && !me.IsHost)
        {
            foreach (var old in _people.Values.Where(p => !p.IsHost && p.Device == device && p.IsCompanion == me.IsCompanion))
            {
                old.EnqueueText("""{"type":"replaced"}""");
                _ = Task.Delay(300).ContinueWith(_ => old.Abort());
            }
        }
        _people[me.Id] = me;
        _ = me.RunSendLoopAsync();
        me.EnqueueText(new JsonObject { ["type"] = "hello", ["id"] = me.Id, ["host"] = me.IsHost }.ToJsonString());
        foreach (var s in _streams.Values)
        {
            if (s.Thumb is { } thumb) me.EnqueueDroppable(thumb);
        }
        BroadcastRoom();

        try
        {
            await foreach (var (data, isText) in me.ReceiveAllAsync(MaxTextBytes, MaxMediaBytes))
            {
                me.Seen();
                if (isText) OnTextMessage(me, data);
                else OnBinaryMessage(me, data);
            }
        }
        catch (Exception ex)
        {
            Tools.Log($"Conexão de participante encerrada por erro: {ex.Message}");
        }

        _people.TryRemove(me.Id, out _);
        me.Abort();
        if (me.Publishing is { } mine) RemoveStream(mine, null);
        BroadcastRoom();
    }

    [GeneratedRegex(@"^[0-9a-f]{8,32}$")]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"^[0-9a-f]{24}$")]
    private static partial Regex ViewKeyPattern();

    [GeneratedRegex(@"^[0-9]{6}$")]
    private static partial Regex PinPattern();

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^gamestream-[a-z0-9-]{1,40}$")]
    private static partial Regex StoreKeyPattern();

    [GeneratedRegex(@"^(avc1\.[0-9a-fA-F]{6}|vp8|vp09\.\d\d\.\d\d\.\d\d|av01\.[0-9A-Za-z.]{5,24})$")]
    private static partial Regex VideoCodecPattern();

    private void OnTextMessage(Participant me, byte[] data)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(data); } catch { return; }
        if (msg is not JsonObject) return;

        string? Str(string key) => msg[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        ushort? StreamNo() => msg["stream"] is JsonValue v && v.TryGetValue<int>(out var n) && n is >= 0 and <= ushort.MaxValue ? (ushort)n : null;

        switch (Str("type"))
        {
            case "hello":   // entrou: a primeira mensagem já traz o nome (e a versão do app, se veio do app)
            case "name":    // trocou de nome
                if (Str("version") is { } version && VersionPattern().IsMatch(version)) me.Version = version;
                me.Name = CleanName(Str("name"), me.IsHost);
                BroadcastRoom();
                break;

            case "pong":
                me.AnswersPing = true;
                break;

            case "watch":
                if (StreamNo() is { } w && _streams.TryGetValue(w, out var toWatch) && toWatch.OwnerId != me.Id)
                {
                    // audioOnly: o app de quem entrou toca o som sozinho (o vídeo vem pela página da sala).
                    var audioOnly = msg["audioOnly"] is JsonValue ao && ao.TryGetValue<bool>(out var b) && b;
                    me.StartWatching(toWatch, audioOnly);
                    if (audioOnly) break;
                    if (toWatch.OwnerId != "" && toWatch.ShouldRequestKeyframe() && _people.TryGetValue(toWatch.OwnerId, out var owner))
                        owner.EnqueueText("""{"type":"keyframe"}""");
                }
                break;

            case "unwatch":
                if (StreamNo() is { } u) me.StopWatching(u);
                break;

            case "publish":
                StartGuestStream(me, msg);
                break;

            case "unpublish":
                if (me.Publishing is { } mine) RemoveStream(mine, null);
                break;

            case "remove":   // só o anfitrião remove alguém da sala
                if (me.IsHost && Str("id") is { } targetId && _people.TryGetValue(targetId, out var target) && !target.IsHost)
                    RemovePerson(target);
                break;

            case "kick":   // só o anfitrião encerra a tela de outra pessoa
                if (me.IsHost && StreamNo() is { } k && k != HostStreamNo && _streams.TryGetValue(k, out var victim))
                    RemoveStream(victim, "O anfitrião encerrou o seu compartilhamento.");
                break;
        }
    }

    /// <summary>Tira alguém da sala: desconecta (a página e o app do mesmo PC) e impede de voltar nesta sessão.</summary>
    private void RemovePerson(Participant target)
    {
        var same = _people.Values
            .Where(p => !p.IsHost && (p.Id == target.Id || (target.Device != "" && p.Device == target.Device) || (target.Ip != "" && p.Ip == target.Ip)))
            .ToList();
        foreach (var p in same)
        {
            if (p.Ip != "") _removedIps[p.Ip] = Environment.TickCount64 + RemovalMs;
            p.EnqueueText("""{"type":"removed"}""");
            _ = Task.Delay(400).ContinueWith(_ => p.Abort());
        }
        Tools.Log($"Anfitrião removeu '{target.Name}' da sala.");
    }

    private void StartGuestStream(Participant me, JsonNode msg)
    {
        string? deny = null;
        if (!AllowGuestShare) deny = "O anfitrião não liberou o compartilhamento de tela.";
        else if (me.Publishing != null) deny = "Você já está compartilhando.";
        else if (_streams.Count >= MaxStreams) deny = $"A sala já tem {MaxStreams} telas compartilhadas.";

        // Só aceitamos formatos conhecidos; o texto do codec é repassado aos outros navegadores.
        var codec = msg["video"]?["codec"] is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : null;
        if (deny == null && (codec == null || !VideoCodecPattern().IsMatch(codec))) deny = "Formato de vídeo não suportado.";

        JsonObject? audio = null;
        if (deny == null && msg["audio"] is JsonObject a &&
            a["sampleRate"] is JsonValue sr && sr.TryGetValue<int>(out var rate) && rate is >= 8000 and <= 96000 &&
            a["numberOfChannels"] is JsonValue ch && ch.TryGetValue<int>(out var channels) && channels is 1 or 2)
        {
            audio = new JsonObject { ["codec"] = "opus", ["sampleRate"] = rate, ["numberOfChannels"] = channels };
        }

        if (deny != null)
        {
            me.EnqueueText(new JsonObject { ["type"] = "unpublished", ["reason"] = deny }.ToJsonString());
            return;
        }

        var stream = new RoomStream((ushort)Interlocked.Increment(ref _nextStreamNo), me.Id);
        stream.SetConfig(new JsonObject { ["video"] = new JsonObject { ["codec"] = codec }, ["audio"] = audio });
        _streams[stream.No] = stream;
        me.Publishing = stream;
        me.EnqueueText(new JsonObject { ["type"] = "published", ["stream"] = stream.No }.ToJsonString());
        BroadcastRoom();
    }

    private void OnBinaryMessage(Participant me, byte[] data)
    {
        var stream = me.Publishing;
        if (stream == null || data.Length <= HeaderSize) return;

        if (!me.AllowBytes(data.Length, MaxPublishBytesPerSecond))
        {
            RemoveStream(stream, "Seu compartilhamento foi encerrado: taxa de envio alta demais.");
            return;
        }

        BitConverter.TryWriteBytes(data.AsSpan(2, 2), stream.No);   // nunca confiamos no número enviado
        switch (data[0])
        {
            case KindVideo:
                FanOut(stream, data, isVideo: true, isKey: (data[1] & 1) != 0);
                break;
            case KindAudio:
                if (stream.HasAudio) FanOut(stream, data, isVideo: false, isKey: false);
                break;
            case KindThumb:
                if (data.Length <= MaxThumbBytes && me.AllowThumbnail()) SetThumbnail(stream, data);
                break;
        }
    }

    /// <summary>Nome da sala aberta (aparece no topo da sala para todos); vazio na transmissão.</summary>
    public string RoomName { get; private set; } = "";

    private static string CleanRoomName(string? name)
    {
        var clean = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length > 32 ? clean[..32].Trim() : clean;
    }

    private static int CountItems(string json)
    {
        try { return JsonNode.Parse(json) is JsonArray a ? a.Count : 0; } catch { return 0; }
    }

    private static string CleanName(string? name, bool isHost)
    {
        var clean = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (clean.Length > 24) clean = clean[..24].Trim();
        return clean.Length > 0 ? clean : isHost ? "Anfitrião" : "Convidado";
    }

    private static string NewToken(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        _directory.Dispose();
        Audio.Dispose();
        if (_app != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(cts.Token);
            await _app.DisposeAsync();
        }
    }

    /// <summary>Uma tela compartilhada na sala (a do app tem dono "" e número 0).</summary>
    private sealed class RoomStream(ushort no, string ownerId)
    {
        private long _lastKeyRequest;

        public ushort No { get; } = no;
        public string OwnerId { get; } = ownerId;
        public bool HasAudio { get; private set; }

        /// <summary>Mensagem "config" pronta para enviar a quem começa a assistir.</summary>
        public string ConfigMessage { get; private set; } = "";

        /// <summary>Última miniatura, já como pacote.</summary>
        public volatile byte[]? Thumb;

        /// <summary>Protege o trecho guardado e a ordem de entrega dos pacotes desta transmissão.</summary>
        public object Sync { get; } = new();

        // Vídeo desde o último quadro-chave. Quem abre a tela recebe isso na hora: a imagem aparece
        // imediatamente, mesmo que a tela esteja parada (sem movimento a captura não gera quadros novos).
        private readonly List<byte[]> _recent = [];
        private long _recentBytes;
        private const long MaxRecentBytes = 12 * 1024 * 1024;

        public void Remember(byte[] packet, bool isKey)
        {
            if (isKey)
            {
                _recent.Clear();
                _recentBytes = 0;
            }
            else if (_recent.Count == 0 || _recentBytes > MaxRecentBytes)
            {
                return;   // ainda sem quadro-chave, ou trecho grande demais: espera o próximo
            }
            _recent.Add(packet);
            _recentBytes += packet.Length;
        }

        public IReadOnlyList<byte[]> Recent => _recentBytes > MaxRecentBytes ? [] : _recent;

        public void SetConfig(JsonObject config)
        {
            lock (Sync)
            {
                _recent.Clear();   // formato novo: o trecho antigo não serve mais
                _recentBytes = 0;
            }
            HasAudio = config["audio"] is JsonObject;
            ConfigMessage = new JsonObject
            {
                ["type"] = "config",
                ["stream"] = No,
                ["video"] = config["video"]?.DeepClone(),
                ["audio"] = config["audio"]?.DeepClone(),
            }.ToJsonString();
        }

        /// <summary>No máximo um pedido de quadro-chave a cada 700 ms.</summary>
        public bool ShouldRequestKeyframe()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastKeyRequest);
            return now - last >= 700 && Interlocked.CompareExchange(ref _lastKeyRequest, now, last) == last;
        }
    }

    /// <summary>Alguém conectado à sala: WebSocket com fila de envio própria, para que um cliente lento não trave os outros.</summary>
    private sealed class Participant(string id, WebSocket socket, bool isHost)
    {
        private readonly record struct Item(byte[] Data, bool IsText, long Generation);

        private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
        private long _queuedBytes;
        private long _generation;

        // Transmissões que este participante está assistindo → "esperando quadro-chave?".
        // Um decodificador só consegue começar num quadro-chave.
        private readonly ConcurrentDictionary<ushort, bool> _watching = new();
        private readonly ConcurrentDictionary<ushort, byte> _audioOnly = new();   // telas em que só recebe o som

        // Controle de taxa do que ele envia.
        private long _windowStart = Environment.TickCount64;
        private long _windowBytes;
        private long _lastThumb;

        public string Id { get; } = id;
        public bool IsHost { get; } = isHost;
        public DateTime JoinedAt { get; } = DateTime.UtcNow;
        public string Name { get; set; } = isHost ? "Anfitrião" : "Convidado";
        public RoomStream? Publishing { get; set; }

        /// <summary>Identificador do PC, quando o participante usa o app ("" no navegador).</summary>
        public string Device { get; init; } = "";

        /// <summary>Conexão do app que só envia a tela (a pessoa aparece pela página da sala).</summary>
        public bool IsCompanion { get; init; }

        /// <summary>Endereço de internet de quem entrou pelo link ("" para conexões deste PC).</summary>
        public string Ip { get; init; } = "";

        /// <summary>Quando chegou a última mensagem dele (Environment.TickCount64).</summary>
        public long LastSeen => Interlocked.Read(ref _lastSeen);
        private long _lastSeen = Environment.TickCount64;
        public void Seen() => Interlocked.Exchange(ref _lastSeen, Environment.TickCount64);

        /// <summary>Versão do app de quem entrou ("" no navegador ou em versões que não informam).</summary>
        public string Version { get; set; } = "";

        /// <summary>Respondeu a um ping: versão que sabe responder (só essas são removidas por silêncio).</summary>
        public volatile bool AnswersPing;

        public void EnqueueText(string text) => Enqueue(Encoding.UTF8.GetBytes(text), true, droppable: false);

        public void EnqueueDroppable(byte[] packet)
        {
            if (Interlocked.Read(ref _queuedBytes) <= BacklogLimit) Enqueue(packet, false, droppable: true);
        }

        public void StartWatching(RoomStream stream, bool audioOnly = false)
        {
            if (audioOnly)
            {
                _audioOnly[stream.No] = 0;
                _watching[stream.No] = false;
                EnqueueText(stream.ConfigMessage);
                return;
            }
            _audioOnly.TryRemove(stream.No, out _);
            lock (stream.Sync)
            {
                EnqueueText(stream.ConfigMessage);
                var recent = stream.Recent;
                foreach (var packet in recent) Enqueue(packet, isText: false, droppable: true);
                // Com o trecho guardado ele já começa num quadro-chave; sem ele, espera o próximo.
                _watching[stream.No] = recent.Count == 0;
            }
        }

        public void StopWatching(ushort no)
        {
            _watching.TryRemove(no, out _);
            _audioOnly.TryRemove(no, out _);
        }

        /// <summary>A transmissão mudou de formato: quem assiste recebe a config nova e recomeça num quadro-chave.</summary>
        public void SendConfigIfWatching(RoomStream stream)
        {
            if (_watching.ContainsKey(stream.No)) StartWatching(stream, _audioOnly.ContainsKey(stream.No));
        }

        /// <summary>Enfileira um pacote se este participante assiste à transmissão. Retorna true se ele precisa de um quadro-chave.</summary>
        public bool OfferMedia(ushort no, byte[] packet, bool isVideo, bool isKey)
        {
            if (!_watching.TryGetValue(no, out var waitingForKey)) return false;
            if (_audioOnly.ContainsKey(no))
            {
                if (!isVideo) Enqueue(packet, isText: false, droppable: true);
                return false;
            }
            if (waitingForKey)
            {
                if (!isVideo || !isKey) return isVideo;
                _watching.TryUpdate(no, false, true);
            }

            if (Interlocked.Read(ref _queuedBytes) > BacklogLimit)
            {
                // A internet dele não está dando conta: descarta o atraso e recomeça no próximo quadro-chave.
                Interlocked.Increment(ref _generation);
                foreach (var key in _watching.Keys) _watching[key] = true;
                return true;
            }

            Enqueue(packet, isText: false, droppable: true);
            return false;
        }

        /// <summary>Conta os bytes recebidos dele no último segundo; false = passou do limite.</summary>
        public bool AllowBytes(int count, long perSecond)
        {
            var now = Environment.TickCount64;
            if (now - _windowStart >= 1000)
            {
                _windowStart = now;
                _windowBytes = 0;
            }
            _windowBytes += count;
            return _windowBytes <= perSecond * 2;   // tolera rajadas (quadros-chave)
        }

        /// <summary>No máximo uma miniatura por segundo.</summary>
        public bool AllowThumbnail()
        {
            var now = Environment.TickCount64;
            if (now - _lastThumb < 1000) return false;
            _lastThumb = now;
            return true;
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

        /// <summary>Lê mensagens completas; encerra se alguma passar do tamanho permitido.</summary>
        public async IAsyncEnumerable<(byte[] Data, bool IsText)> ReceiveAllAsync(int maxText, int maxBinary)
        {
            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r;
                try { r = await socket.ReceiveAsync(buffer, CancellationToken.None); }
                catch { yield break; }

                if (r.MessageType == WebSocketMessageType.Close) yield break;
                ms.Write(buffer, 0, r.Count);
                var isText = r.MessageType == WebSocketMessageType.Text;
                if (ms.Length > (isText ? maxText : maxBinary)) yield break;
                if (!r.EndOfMessage) continue;

                var data = ms.ToArray();
                ms.SetLength(0);
                yield return (data, isText);
            }
        }

        public void Abort()
        {
            _queue.Writer.TryComplete();
            try { socket.Abort(); } catch { }
        }
    }
}
