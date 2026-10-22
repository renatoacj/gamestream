using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace GameStream;

/// <summary>
/// Avisa quando há uma versão nova: consulta a última versão publicada no GitHub ao abrir o app e a cada 6 horas.
/// Só lê a página pública de versões; pode ser desligado em Configurações (ou na instalação).
/// </summary>
internal static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/renatoacj/gamestream/releases/latest";
    private const string ReleasesPage = "https://github.com/renatoacj/gamestream/releases/";
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Versão mais nova que a instalada ("0.5.12"), ou null.</summary>
    public static string? NewVersion { get; private set; }

    /// <summary>Página da versão nova no GitHub.</summary>
    public static string? NewVersionUrl { get; private set; }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GameStream/{AppVersion.Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static void Start(CancellationToken token) => _ = LoopAsync(token);

    private static async Task LoopAsync(CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), token); } catch (OperationCanceledException) { return; }
        while (!token.IsCancellationRequested)
        {
            if (AppSettings.Current.CheckUpdatesEnabled) await CheckAsync(token);
            try { await Task.Delay(TimeSpan.FromHours(6), token); } catch (OperationCanceledException) { return; }
        }
    }

    public static async Task CheckAsync(CancellationToken token = default)
    {
        try
        {
            var json = JsonNode.Parse(await Http.GetStringAsync(LatestApi, token));
            var tag = json?["tag_name"]?.GetValue<string>()?.TrimStart('v') ?? "";
            var url = json?["html_url"]?.GetValue<string>() ?? "";
            if (!Version.TryParse(tag, out var latest) || !Version.TryParse(AppVersion.Current, out var mine)) return;
            if (latest > mine && url.StartsWith(ReleasesPage, StringComparison.Ordinal))
            {
                if (NewVersion != tag) Tools.Log($"Versão nova disponível: {tag}.");
                (NewVersion, NewVersionUrl) = (tag, url);
            }
            else
            {
                (NewVersion, NewVersionUrl) = (null, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sem internet ou GitHub fora do ar: tenta de novo mais tarde.
        }
    }

    /// <summary>Abre a página da versão nova no navegador padrão (só páginas de versão do próprio projeto).</summary>
    public static bool OpenDownloadPage()
    {
        if (NewVersionUrl is not { } url || !url.StartsWith(ReleasesPage, StringComparison.Ordinal)) return false;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }
}
