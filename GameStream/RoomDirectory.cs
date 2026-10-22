using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GameStream;

/// <summary>
/// Código fixo das salas salvas ("nome-da-sala-&lt;guid&gt;").
///
/// O endereço do túnel da Cloudflare muda toda vez que o app abre. Para o código da sala continuar valendo,
/// o app de quem criou a sala publica o endereço atual num canal do ntfy.sh (serviço público de mensagens,
/// sem conta) e quem tem o código lê de lá. Tudo sai do GUID, que só quem tem o código conhece:
///   - o nome do canal é um hash do GUID (o ntfy não sabe de que sala se trata);
///   - a mensagem vai criptografada (AES-GCM) com uma chave tirada do GUID;
///   - a chave de entrada da sala também sai do GUID, então quem tem o código entra sem pedir nada.
/// Quem ler o canal sem o código só vê texto embaralhado.
/// </summary>
internal static partial class RoomIds
{
    [GeneratedRegex(@"(?:^|-)([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex GuidAtEnd();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^https://[a-z0-9-]+\.trycloudflare\.com$")]
    private static partial Regex TunnelOrigin();

    public static bool IsGuid(string? text) => text != null && GuidPattern().IsMatch(text);

    /// <summary>O GUID no fim de um código fixo ("sexta-do-division-3f2a…"), ou null.</summary>
    public static string? GuidFromCode(string code) =>
        GuidAtEnd().Match(code.Trim().ToLowerInvariant()) is { Success: true } m ? m.Groups[1].Value : null;

    public static bool IsTunnelOrigin(string? url) => url != null && TunnelOrigin().IsMatch(url);

    private static byte[] Derive(string guid, string purpose) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(guid), Encoding.UTF8.GetBytes("gamestream/" + purpose));

    /// <summary>Chave longa de entrada (o "?k=" do link) desta sala.</summary>
    public static string ViewKey(string guid) => Convert.ToHexString(Derive(guid, "view"))[..24].ToLowerInvariant();

    /// <summary>Senha curta (6 números) desta sala.</summary>
    public static string Pin(string guid) =>
        (BitConverter.ToUInt64(Derive(guid, "pin"), 0) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static string Topic(string guid) => "gs" + Convert.ToHexString(Derive(guid, "topic"))[..40].ToLowerInvariant();

    /// <summary>Código para passar aos amigos: o nome da sala (se houver) + "-" + o GUID.</summary>
    public static string Code(string roomName, string guid)
    {
        var slug = Slug(roomName);
        return slug == "" ? guid : $"{slug}-{guid}";
    }

    private static string Slug(string name)
    {
        var plain = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;   // tira acentos
            var c = char.ToLowerInvariant(ch);
            plain.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }
        var slug = Regex.Replace(plain.ToString(), "-+", "-").Trim('-');
        return slug.Length > 24 ? slug[..24].TrimEnd('-') : slug;
    }

    public static string Seal(string guid, string text)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(text);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Derive(guid, "seal"), 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public static string? Open(string guid, string sealedText)
    {
        try
        {
            var data = Convert.FromBase64String(sealedText.Trim());
            if (data.Length < 12 + 16) return null;
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(Derive(guid, "seal"), 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(12 + plain.Length), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;   // de outra sala, adulterada ou lixo
        }
    }
}

/// <summary>
/// Publica no ntfy.sh o endereço atual de cada sala salva (enquanto o app estiver aberto) e lê o
/// endereço de uma sala a partir do código. Ver <see cref="RoomIds"/>.
/// </summary>
internal sealed class RoomDirectory : IDisposable
{
    private const string Server = "https://ntfy.sh";
    private static readonly TimeSpan Republish = TimeSpan.FromHours(6);   // o ntfy guarda mensagens por 12 h
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Func<string?> _currentUrl;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (string Url, DateTime At)> _published = [];
    private volatile string[] _rooms = [];

    public RoomDirectory(Func<string?> currentUrl)
    {
        _currentUrl = currentUrl;
        _ = RunAsync(_cts.Token);
    }

    /// <summary>As salas salvas deste PC (os GUIDs); o endereço é publicado para cada uma.</summary>
    public void SetRooms(IEnumerable<string> guids) => _rooms = guids.Where(RoomIds.IsGuid).Distinct().Take(50).ToArray();

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch (OperationCanceledException) { break; }
            var url = _currentUrl();
            if (!RoomIds.IsTunnelOrigin(url)) continue;
            if (!AppSettings.Current.FixedRoomCodesEnabled) continue;   // desligado pelo usuário (privacidade)
            foreach (var guid in _rooms)
            {
                if (_published.TryGetValue(guid, out var last) && last.Url == url && DateTime.UtcNow - last.At < Republish) continue;
                try
                {
                    var body = new JsonObject { ["u"] = url, ["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }.ToJsonString();
                    using var res = await Http.PostAsync($"{Server}/{RoomIds.Topic(guid)}",
                        new StringContent(RoomIds.Seal(guid, body), Encoding.UTF8, "text/plain"), token);
                    res.EnsureSuccessStatusCode();
                    _published[guid] = (url!, DateTime.UtcNow);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Tenta de novo daqui a um minuto (marca como publicado "há quase 6 h").
                    _published[guid] = (url!, DateTime.UtcNow - Republish + TimeSpan.FromMinutes(1));
                    Tools.Log("Não foi possível publicar o endereço da sala no ntfy.sh: " + ex.Message);
                }
            }
        }
    }

    /// <summary>Endereço atual da sala com este GUID (https://….trycloudflare.com), ou null se não houver.</summary>
    public static async Task<string?> ResolveAsync(string guid, CancellationToken token)
    {
        using var res = await Http.GetAsync($"{Server}/{RoomIds.Topic(guid)}/json?poll=1&since=all", token);
        if (!res.IsSuccessStatusCode) return null;
        var text = await res.Content.ReadAsStringAsync(token);
        string? best = null;
        long bestAt = long.MinValue;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var msg = JsonNode.Parse(line);
                if (msg?["event"]?.GetValue<string>() != "message") continue;
                if (RoomIds.Open(guid, msg["message"]?.GetValue<string>() ?? "") is not { } plain) continue;
                var data = JsonNode.Parse(plain);
                var url = data?["u"]?.GetValue<string>();
                var at = data?["t"]?.GetValue<long>() ?? 0;
                // Só endereços de túnel da Cloudflare: o app nunca é apontado para um site qualquer.
                if (RoomIds.IsTunnelOrigin(url) && at > bestAt) (best, bestAt) = (url, at);
            }
            catch
            {
                // linha que não é nossa
            }
        }
        return best;
    }

    public void Dispose() => _cts.Cancel();
}
