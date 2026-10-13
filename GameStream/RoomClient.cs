using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace GameStream;

/// <summary>Destino da transmissão do app: a sala local (<see cref="StreamServer"/>) ou a sala de outra pessoa (<see cref="RoomClient"/>).</summary>
public interface IRoomSink
{
    /// <param name="config">{ video: {codec}, audio: {...}|null }</param>
    void PublishConfig(JsonObject config);
    void PublishMedia(byte[] packet, bool isVideo, bool isKey);
    void PublishThumbnail(byte[] jpeg);
    void PublishOffline();
}

/// <summary>
/// Liga o app à sala criada por outra pessoa: conecta ao servidor dela (pelo link público)
/// como participante e envia a transmissão capturada aqui, no mesmo protocolo que o navegador usa.
/// </summary>
public sealed partial class RoomClient : IRoomSink, IDisposable
{
    /// <summary>Se o envio acumular mais que isso, a internet não está dando conta: pula vídeo até o próximo quadro-chave.</summary>
    private const long BacklogLimit = 3 * 1024 * 1024;

    private readonly Uri _wsUri;
    private readonly string _checkUrl;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(byte[] Data, bool IsText)> _queue =
        Channel.CreateUnbounded<(byte[], bool)>(new UnboundedChannelOptions { SingleReader = true });
    private long _queuedBytes;
    private volatile bool _dropUntilKey;
    private volatile JsonObject? _config;      // transmissão em andamento (reenviada ao reconectar)
    private volatile bool _connected;

    /// <summary>Endereço da sala para mostrar dentro do app (a mesma página que os convidados veem).</summary>
    public string RoomPageUrl { get; }

    /// <summary>Origem do servidor da sala (https://xxxx.trycloudflare.com).</summary>
    public string Origin { get; }

    public string State { get; private set; } = "connecting";   // connecting | connected | closed
    public string? Message { get; private set; }

    /// <summary>A sala recusou ou encerrou o nosso compartilhamento (motivo para mostrar ao usuário).</summary>
    public event Action<string>? PublishRejected;

    // Só aceitamos links de sala do GameStream (túnel da Cloudflare): o app nunca é apontado para um site qualquer.
    [GeneratedRegex(@"^https://[a-z0-9-]+\.trycloudflare\.com$")]
    private static partial Regex OriginPattern();

    // Chave longa (link) ou senha curta de 6 números (código da sala).
    [GeneratedRegex(@"^([0-9a-f]{16,64}|\d{6})$")]
    private static partial Regex KeyPattern();

    // Código da sala: palavras do túnel + "-" + 6 números (ex.: spears-manhattan-calgary-until-482913).
    [GeneratedRegex(@"^([a-z0-9]+(?:-[a-z0-9]+)*)-(\d{6})$")]
    private static partial Regex CodePattern();

    /// <summary>As palavras do endereço do túnel ("https://a-b-c-d.trycloudflare.com" → "a-b-c-d"), ou null.</summary>
    public static string? TunnelName(string origin) =>
        OriginPattern().IsMatch(origin) ? origin["https://".Length..^".trycloudflare.com".Length] : null;

    /// <summary>Valida o link colado pelo usuário. Retorna null (e a mensagem de erro) se não for um link de sala.</summary>
    public static RoomClient? TryCreate(string link, string name, out string error)
    {
        error = "Não reconheci isso como uma sala. Cole o código da sala (palavras-e-números) ou o link completo.";

        // Código da sala → o mesmo endereço do link, com a senha curta no lugar da chave longa.
        var text = link.Trim().ToLowerInvariant().Replace(" ", "");
        if (CodePattern().Match(text) is { Success: true } code)
            link = $"https://{code.Groups[1].Value}.trycloudflare.com/?k={code.Groups[2].Value}";

        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)) return null;

        var origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        var key = uri.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .FirstOrDefault(p => p.Length == 2 && p[0] == "k")?[1] ?? "";
        if (!OriginPattern().IsMatch(origin) || !KeyPattern().IsMatch(key)) return null;

        error = "";
        return new RoomClient(origin, key, name);
    }

    private RoomClient(string origin, string key, string name)
    {
        // "dev" identifica este PC na sala: liga a conexão do app (que envia a tela) à página
        // da sala aberta dentro do app, para as duas aparecerem como uma pessoa só.
        var device = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        Origin = origin;
        _name = name;
        _wsUri = new Uri($"wss://{origin["https://".Length..]}/ws?role=viewer&k={key}&dev={device}&pub=1");
        RoomPageUrl = $"{origin}/?k={key}&dev={device}&app=1&name={Uri.EscapeDataString(name)}";
        _checkUrl = $"{origin}/api/check?k={key}";
        _ = RunAsync();
    }

    /// <summary>Mantém a conexão; se cair, tenta de novo e retoma a transmissão em andamento.</summary>
    private async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(_wsUri, timeout.Token);
                }

                // Fila limpa a cada conexão; quem estava transmitindo publica de novo.
                while (_queue.Reader.TryRead(out var stale)) Interlocked.Add(ref _queuedBytes, -stale.Data.Length);
                _dropUntilKey = true;
                _connected = true;
                State = "connected";
                Message = null;
                SendText(new JsonObject { ["type"] = "hello", ["name"] = _name });
                if (_config is { } config) SendPublish(config);

                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                var sending = SendLoopAsync(ws, session.Token);
                var fatal = await ReceiveLoopAsync(ws, session.Token);
                session.Cancel();
                await sending.ContinueWith(_ => { }, TaskScheduler.Default);
                if (fatal) break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Tools.Log("Sala remota: " + ex.Message);
                if (await KeyRejectedAsync(token))
                {
                    // A sala existe, mas recusou a chave: não adianta insistir (e tentativas erradas
                    // demais bloqueiam a entrada por código para todo mundo).
                    Message = "Código ou link incorreto. Confira com quem criou a sala (o código muda se ela gerar um novo).";
                    break;
                }
                Message = "Não foi possível conectar à sala. Confira o código e a internet. Tentando de novo…";
            }

            _connected = false;
            if (token.IsCancellationRequested) break;
            State = "connecting";
            Message ??= "A conexão com a sala caiu. Tentando de novo…";
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); } catch (OperationCanceledException) { break; }
        }
        _connected = false;
        State = "closed";
    }

    /// <summary>
    /// A conexão falhou: pergunta à sala se a chave vale. Só retorna true quando a sala respondeu
    /// claramente "não" (401); sem resposta (sala fechada, sem internet) continua tentando.
    /// </summary>
    private async Task<bool> KeyRejectedAsync(CancellationToken token)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var res = await http.GetAsync(_checkUrl, token);
            return res.StatusCode == System.Net.HttpStatusCode.Unauthorized;
        }
        catch
        {
            return false;
        }
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        try
        {
            await foreach (var (data, isText) in _queue.Reader.ReadAllAsync(token))
            {
                Interlocked.Add(ref _queuedBytes, -data.Length);
                await ws.SendAsync(data, isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, token);
            }
        }
        catch
        {
            ws.Abort();   // derruba o recebimento também, para a reconexão começar
        }
    }

    /// <summary>Retorna true quando não adianta reconectar (link trocado).</summary>
    private async Task<bool> ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buffer, token); }
            catch { return false; }
            if (r.MessageType == WebSocketMessageType.Close) return false;

            // Só nos interessam os avisos em texto; miniaturas e mídia de outras telas são ignoradas.
            if (r.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 256 * 1024) { ms.SetLength(0); continue; }
            if (!r.EndOfMessage) continue;

            JsonNode? msg = null;
            try { msg = JsonNode.Parse(ms.ToArray()); } catch { /* ignora mensagem malformada */ }
            ms.SetLength(0);

            switch (msg?["type"]?.GetValue<string>())
            {
                case "unpublished":
                    _config = null;
                    PublishRejected?.Invoke(msg["reason"]?.GetValue<string>() ?? "A sala encerrou o seu compartilhamento.");
                    break;
                case "full":
                    Message = "A sala está cheia. Tentando entrar de novo…";
                    return false;
                case "expired":
                    Message = "Este código não vale mais: quem criou a sala gerou um novo.";
                    return true;
                case "removed":
                    _config = null;
                    Message = "Você foi removido da sala por quem a criou.";
                    PublishRejected?.Invoke(Message);
                    return true;
            }
        }
        return false;
    }

    private void SendText(JsonObject msg) => Enqueue(Encoding.UTF8.GetBytes(msg.ToJsonString()), true);

    private void Enqueue(byte[] data, bool isText)
    {
        Interlocked.Add(ref _queuedBytes, data.Length);
        _queue.Writer.TryWrite((data, isText));
    }

    private void SendPublish(JsonObject config)
    {
        SendText(new JsonObject { ["type"] = "unpublish" });   // caso a sala ainda tenha a transmissão anterior
        SendText(new JsonObject
        {
            ["type"] = "publish",
            ["video"] = config["video"]?.DeepClone(),
            ["audio"] = config["audio"]?.DeepClone(),
        });
        _dropUntilKey = true;
    }

    // ---------- IRoomSink ----------

    public void PublishConfig(JsonObject config)
    {
        _config = config;
        if (_connected) SendPublish(config);
    }

    public void PublishMedia(byte[] packet, bool isVideo, bool isKey)
    {
        if (!_connected || _config == null) return;
        if (isVideo)
        {
            if (Interlocked.Read(ref _queuedBytes) > BacklogLimit) _dropUntilKey = true;
            if (_dropUntilKey)
            {
                if (!isKey || Interlocked.Read(ref _queuedBytes) > BacklogLimit) return;
                _dropUntilKey = false;
            }
        }
        Enqueue(packet, false);
    }

    public void PublishThumbnail(byte[] jpeg)
    {
        if (_connected && _config != null)
            Enqueue(StreamServer.BuildPacket(StreamServer.KindThumb, false, 0, jpeg), false);
    }

    public void PublishOffline()
    {
        if (_config == null) return;
        _config = null;
        if (_connected) SendText(new JsonObject { ["type"] = "unpublish" });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
    }
}
