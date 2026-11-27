using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace GameStream;

/// <summary>
/// Estado das salas da lista de recentes: "live" (alguém transmitindo), "open" (aberta, sem ninguém transmitindo),
/// "closed" (fechada pelo dono, mas o app dele está aberto), "offline" (fora do ar) ou "invalid" (código não vale mais).
/// Consulta a sala do amigo (e, para código fixo, o endereço atual no ntfy.sh); guarda a resposta por 25 s.
/// </summary>
internal static class RoomStatus
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly ConcurrentDictionary<string, (DateTime At, JsonObject Result)> Cache = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, string? Origin)> Addresses = new();

    public static async Task<JsonObject> GetAsync(string code)
    {
        if (Cache.TryGetValue(code, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(25)) return hit.Result.DeepClone().AsObject();
        var result = await ProbeAsync(code);
        Cache[code] = (DateTime.UtcNow, result);
        return result.DeepClone().AsObject();
    }

    private static JsonObject State(string state, int people = 0) => new() { ["state"] = state, ["people"] = people };

    private static async Task<JsonObject> ProbeAsync(string code)
    {
        if (!RoomClient.TryParse(code, out var origin, out var key, out var guid)) return State("invalid");
        try
        {
            if (guid != null)
            {
                // Endereço atual da sala: o ntfy.sh só precisa ser consultado de vez em quando.
                if (!Addresses.TryGetValue(guid, out var known) || DateTime.UtcNow - known.At > TimeSpan.FromMinutes(2))
                {
                    known = (DateTime.UtcNow, await RoomDirectory.ResolveAsync(guid, CancellationToken.None));
                    Addresses[guid] = known;
                }
                origin = known.Origin;
                if (origin == null) return State("offline");
            }

            using var res = await Http.GetAsync($"{origin}/api/peek?k={key}");
            if ((int)res.StatusCode == 404)
            {
                // Sala numa versão antiga (sem /api/peek): só dá para saber se está aberta.
                using var check = await Http.GetAsync($"{origin}/api/check?k={key}");
                return (int)check.StatusCode switch
                {
                    204 => State("open"),
                    423 => State("closed"),
                    401 => State("invalid"),
                    _ => State("offline"),
                };
            }
            if ((int)res.StatusCode == 401) return State("invalid");
            if (!res.IsSuccessStatusCode) return State("offline");
            var peek = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            if (peek?["open"]?.GetValue<bool>() != true) return State("closed");
            var people = peek["people"]?.GetValue<int>() ?? 0;
            return State(peek["live"]?.GetValue<bool>() == true ? "live" : "open", people);
        }
        catch
        {
            if (guid != null) Addresses.TryRemove(guid, out _);   // pode ter mudado de endereço
            return State("offline");
        }
    }
}
