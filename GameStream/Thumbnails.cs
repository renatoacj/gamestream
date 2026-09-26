using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace GameStream;

/// <summary>
/// Miniaturas (JPEG 320×180) das janelas e monitores para a tela de escolha da fonte.
/// Usa o mesmo gfxcapture da transmissão, então funciona também com jogos (DirectX).
/// </summary>
public static class Thumbnails
{
    private static readonly SemaphoreSlim Parallel = new(4, 4);
    private static readonly ConcurrentDictionary<string, (DateTime Time, byte[] Jpeg)> Cache = new();
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    /// <summary>JPEG da fonte, ou null se não deu para capturar (ex.: janela minimizada).</summary>
    public static async Task<byte[]?> GetAsync(string id)
    {
        if (Cache.TryGetValue(id, out var hit) && DateTime.UtcNow - hit.Time < MaxAge) return hit.Jpeg;
        if (id.Length < 3 || !long.TryParse(id[2..], out var handle)) return null;

        var input = id[0] == 'w' ? $"hwnd={handle}" : $"hmonitor={handle}";
        var ffmpeg = await Tools.EnsureFfmpegAsync();

        await Parallel.WaitAsync();
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpeg,
                "-hide_banner -loglevel error " +
                $"-f lavfi -i gfxcapture={input}:width=320:height=180:resize_mode=scale_aspect:capture_cursor=0:max_framerate=30 " +
                "-frames:v 1 -vf hwdownload,format=bgra,format=yuvj420p -c:v mjpeg -q:v 5 -f image2pipe pipe:1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
            await copy;

            if (p.ExitCode != 0 || ms.Length == 0) return null;
            var jpeg = ms.ToArray();
            Cache[id] = (DateTime.UtcNow, jpeg);
            return jpeg;
        }
        finally
        {
            Parallel.Release();
        }
    }
}
