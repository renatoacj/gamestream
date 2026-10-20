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

    private readonly string _key;
    private readonly string _device;
    private readonly string? _guid;      // código fixo: o endereço é procurado no ntfy.sh (ver RoomDirectory)
    private string? _origin;
    private bool _resolveNeeded;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(byte[] Data, bool IsText)> _queue =
        Channel.CreateUnbounded<(byte[], bool)>(new UnboundedChannelOptions { SingleReader = true });
    private long _queuedBytes;
    private volatile bool _dropUntilKey;
    private volatile JsonObject? _config;      // transmissão em andamento (reenviada ao reconectar)
    private volatile bool _connected;

    /// <summary>
    /// Página da sala para mostrar dentro do app, ou null se ainda não achou a sala. É a página DESTE app (servida
    /// localmente) conectada ao servidor do amigo: funciona igual mesmo que o amigo esteja numa versão mais antiga
    /// (e o som é tocado por este app; ver RoomAudioPlayer).
    /// </summary>
    public string? RoomPageUrl => _origin == null ? null
        : $"/?remote={Uri.EscapeDataString(_origin)}&k={_key}&dev={_device}&app=1&name={Uri.EscapeDataString(_name)}";

    /// <summary>Origem do servidor da sala (https://xxxx.trycloudflare.com). Com código fixo, muda quando quem criou reabre o app.</summary>
    public string? Origin => _origin;

    private Uri WsUri => new($"wss://{_origin!["https://".Length..]}/ws?role=viewer&k={_key}&dev={_device}&pub=1");
    private string CheckUrl => $"{_origin}/api/check?k={_key}";

    public string State { get; private set; } = "connecting";   // connecting | connected | closed

    /// <summary>Nome de quem criou a sala (aparece no título e na lista de salas recentes).</summary>
    public string? HostName { get; private set; }
    /// <summary>Por que não está conectado: "gone" (a sala saiu do ar), "closed" (fechada, vai reabrir) ou null.</summary>
    public string? Reason { get; private set; }

    public string? Message { get; private set; }

    /// <summary>Onde tocar o som da tela escolhida (o app toca, não a página; ver <see cref="RoomAudioPlayer"/>).</summary>
    public RoomAudioPlayer? Audio { get; set; }

    private volatile int _listening = -1;   // tela cujo som recebemos (-1 = nenhuma)

    /// <summary>Passa a receber só o som desta tela (o vídeo continua vindo pela página da sala).</summary>
    public void Listen(ushort? stream)
    {
        var next = stream ?? -1;
        var previous = _listening;
        if (next == previous) return;
        _listening = next;
        if (!_connected) return;   // ao conectar, o pedido é feito de novo
        if (previous >= 0) SendText(new JsonObject { ["type"] = "unwatch", ["stream"] = previous });
        if (next >= 0) SendText(new JsonObject { ["type"] = "watch", ["stream"] = next, ["audioOnly"] = true });
    }

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

        // Código fixo de uma sala salva ("nome-da-sala-<guid>"): o endereço é procurado depois, no ntfy.sh.
        var text = link.Trim().ToLowerInvariant().Replace(" ", "");
        if (RoomIds.GuidFromCode(text) is { } guid)
        {
            error = "";
            return new RoomClient(null, RoomIds.ViewKey(guid), name, guid);
        }

        // Código da sala → o mesmo endereço do link, com a senha curta no lugar da chave longa.
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

    private RoomClient(string? origin, string key, string name, string? guid = null)
    {
        // "dev" identifica este PC na sala: liga a conexão do app (que envia a tela) à página
        // da sala aberta dentro do app, para as duas aparecerem como uma pessoa só.
        _device = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _origin = origin;
        _key = key;
        _guid = guid;
        _name = name;
        _ = RunAsync();
    }

    /// <summary>Mantém a conexão; se cair, tenta de novo e retoma a transmissão em andamento.</summary>
    private async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            // Código fixo: procura o endereço atual da sala (na primeira vez e depois que ela sai do ar).
            if (_guid != null && (_origin == null || _resolveNeeded))
            {
                string? found = null;
                try
                {
                    found = await RoomDirectory.ResolveAsync(_guid, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Tools.Log("Sala remota (procurando o endereço): " + ex.Message);
                }
                if (found == null)
                {
                    State = "connecting";
                    Reason = "gone";
                    Message = "Quem criou a sala está com o GameStream fechado. Você entra sozinho quando ele abrir.";
                    try { await Task.Delay(TimeSpan.FromSeconds(15), token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                _origin = found;
                _resolveNeeded = false;
            }

            using var ws = new ClientWebSocket();
            try
            {
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(WsUri, timeout.Token);
                }

                // Fila limpa a cada conexão; quem estava transmitindo publica de novo.
                while (_queue.Reader.TryRead(out var stale)) Interlocked.Add(ref _queuedBytes, -stale.Data.Length);
                _dropUntilKey = true;
                _connected = true;
                State = "connected";
                Message = null;
                Reason = null;
                SendText(new JsonObject { ["type"] = "hello", ["name"] = _name });
                if (_config is { } config) SendPublish(config);
                if (_listening >= 0) SendText(new JsonObject { ["type"] = "watch", ["stream"] = _listening, ["audioOnly"] = true });

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
                var check = await CheckKeyAsync(token);
                if (check == 401)
                {
                    // A sala existe, mas recusou a chave: não adianta insistir (e tentativas erradas
                    // demais bloqueiam a entrada por código para todo mundo).
                    Message = _guid != null
                        ? "Este código não vale mais: quem criou a sala apagou a sala ou gerou um código novo."
                        : "Código ou link incorreto. Confira com quem criou a sala (o código muda se ela gerar um novo).";
                    break;
                }
                if (_guid != null && check is null or 530 or 502 or 404)
                {
                    // Código fixo: a sala mudou de endereço (quem criou reabriu o app) ou ainda está fechada.
                    _resolveNeeded = true;
                    Reason = "gone";
                    Message = "Quem criou a sala está com o GameStream fechado ou a internet dele caiu. Você entra sozinho quando ele voltar.";
                }
                else if (check is 530 or 502 or 404)
                {
                    // A Cloudflare não acha mais o túnel (erro 1033): quem criou a sala fechou o app ou a
                    // conexão dele caiu, e o endereço mudou. Continua tentando caso tenha sido só um instante.
                    Reason = "gone";
                    Message = "Se quem a criou fechou o GameStream ou a internet dele caiu, o código mudou: peça o novo.";
                }
                else
                {
                    Reason = null;
                    Message = "Não foi possível conectar à sala. Confira o código e a internet. Tentando de novo…";
                }
            }

            _connected = false;
            if (token.IsCancellationRequested) break;
            State = "connecting";
            Message ??= "A conexão com a sala caiu. Tentando de novo…";
            var wait = _resolveNeeded ? 10 : _connected ? 3 : 5;
            try { await Task.Delay(TimeSpan.FromSeconds(wait), token); } catch (OperationCanceledException) { break; }
        }
        _connected = false;
        State = "closed";
    }

    /// <summary>
    /// A conexão falhou: pergunta à sala se a chave vale. Retorna o código HTTP da resposta
    /// (401 = chave errada; 423 = sala fechada; 530 = o túnel não existe mais) ou null sem resposta.
    /// </summary>
    private async Task<int?> CheckKeyAsync(CancellationToken token)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var res = await http.GetAsync(CheckUrl, token);
            return (int)res.StatusCode;
        }
        catch
        {
            return null;
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
        using var bin = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buffer, token); }
            catch { return false; }
            if (r.MessageType == WebSocketMessageType.Close) return false;

            // Binário: só o som da tela escolhida interessa (vai para o RoomAudioPlayer); o resto é ignorado.
            if (r.MessageType != WebSocketMessageType.Text)
            {
                bin.Write(buffer, 0, r.Count);
                if (bin.Length > 512 * 1024) bin.SetLength(0);
                if (!r.EndOfMessage) continue;
                var packet = bin.ToArray();
                bin.SetLength(0);
                if (_listening >= 0 && packet.Length > 12 && packet[0] == 2) Audio?.OnPacket(packet);
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
                case "room":
                    HostName = (msg["people"] as JsonArray)?
                        .FirstOrDefault(p => p?["host"]?.GetValue<bool>() == true)?["name"]?.GetValue<string>() ?? HostName;
                    break;
                case "unpublished":
                    _config = null;
                    PublishRejected?.Invoke(msg["reason"]?.GetValue<string>() ?? "A sala encerrou o seu compartilhamento.");
                    break;
                case "full":
                    Message = "A sala está cheia. Tentando entrar de novo…";
                    return false;
                case "closed":
                    Reason = "closed";
                    Message = "Quando quem a criou abrir de novo, você entra sozinho.";
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
        var previous = _config;
        _config = config;
        if (!_connected) return;
        if (previous != null)
        {
            // Trocou de janela/monitor sem parar: o mesmo formato continua no mesmo compartilhamento (a sala só vê a
            // imagem mudar). Formato diferente (ex.: o áudio ligou ou desligou): encerra e compartilha de novo.
            if (JsonNode.DeepEquals(previous, config)) return;
            SendText(new JsonObject { ["type"] = "unpublish" });
        }
        SendPublish(config);
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
