using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace GameStream;

/// <summary>
/// Cria um link público HTTPS gratuito com o Cloudflare Quick Tunnel
/// (https://try.cloudflare.com): não precisa de conta, de abrir portas no
/// roteador nem de instalar nada. Na primeira execução baixa o cloudflared
/// oficial do GitHub da Cloudflare.
/// </summary>
public sealed partial class PublicTunnel : IDisposable
{
    private const string DownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    private readonly int _localPort;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;

    public string? Url { get; private set; }
    public string Status { get; private set; } = "Iniciando…";

    public PublicTunnel(int localPort) => _localPort = localPort;

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com")]
    private static partial Regex UrlPattern();

    /// <summary>Mantém o túnel no ar, recriando se o cloudflared cair.</summary>
    public async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var exe = await EnsureCloudflaredAsync(token);
                Status = "Criando link público…";
                await RunCloudflaredAsync(exe, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Status = "Erro no link público: " + ex.Message;
            }

            Url = null;
            if (!token.IsCancellationRequested)
            {
                Status = "Link público caiu, tentando de novo…";
                await Task.Delay(TimeSpan.FromSeconds(5), token).ContinueWith(_ => { });
            }
        }
    }

    private const string Publisher = "Cloudflare, Inc.";

    private async Task<string> EnsureCloudflaredAsync(CancellationToken token)
    {
        // Só executamos um cloudflared com assinatura digital válida da Cloudflare,
        // esteja ele ao lado do app, já baixado ou recém-baixado.

        // 1) ao lado do GameStream.exe (quem distribui pode incluir junto)
        var local = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
        if (File.Exists(local) && Tools.IsSignedBy(local, Publisher)) return local;

        // 2) já baixado antes
        var cached = Path.Combine(Tools.DataDir, "cloudflared.exe");
        if (File.Exists(cached))
        {
            if (Tools.IsSignedBy(cached, Publisher)) return cached;
            File.Delete(cached);   // alterado ou corrompido: baixa de novo
        }

        // 3) baixa a versão oficial
        Directory.CreateDirectory(Tools.DataDir);
        var temp = cached + ".download";
        await Tools.DownloadAsync(DownloadUrl, temp, pct => Status = $"Baixando cloudflared (só na primeira vez)… {pct}%", token);
        if (!Tools.IsSignedBy(temp, Publisher))
        {
            File.Delete(temp);
            throw new InvalidDataException("o cloudflared baixado não tem a assinatura digital da Cloudflare");
        }
        File.Move(temp, cached, overwrite: true);
        return cached;
    }

    private async Task RunCloudflaredAsync(string exe, CancellationToken token)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "tunnel", "--no-autoupdate", "--url", $"http://localhost:{_localPort}" })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("não foi possível iniciar o cloudflared");
        _process = process;
        using var _ = token.Register(() => { try { process.Kill(); } catch { } });

        // O cloudflared escreve o endereço do túnel no stderr.
        process.OutputDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        string? line;
        while ((line = await process.StandardError.ReadLineAsync(token)) != null)
        {
            var match = UrlPattern().Match(line);
            if (match.Success && Url == null)
            {
                Url = match.Value;
                Status = "Online";
            }
        }
        await process.WaitForExitAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _process?.Kill(); } catch { }
    }
}
