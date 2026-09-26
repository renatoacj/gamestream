using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GameStream;

/// <summary>
/// Captura uma janela ou monitor com o FFmpeg (gfxcapture = Windows Graphics Capture) e
/// codifica em H.264. A captura e a mudança de resolução acontecem na GPU; com codificador
/// de GPU a imagem nem sai da placa, o que permite 60 fps com o jogo rodando.
/// A saída é H.264 "Annex B" com um delimitador (AUD) antes de cada quadro, que usamos
/// para separar os quadros.
/// </summary>
public sealed class VideoCapture : IDisposable
{
    private sealed record EncoderDef(string Id, string Label, Func<StreamSettings, string> Args);

    private static readonly EncoderDef[] Encoders =
    [
        new("nvenc", "NVIDIA NVENC", s =>
            $"-c:v h264_nvenc -preset {Pick(s, "p2", "p4", "p6")} -tune ll -zerolatency 1 -bf 0 " +
            $"-profile:v {s.Profile} {(s.RateControl == "vbr" ? "-rc vbr" : "-rc cbr")} {Rate(s)}"),
        new("amf", "AMD AMF", s =>
            $"-c:v h264_amf -usage ultralowlatency -quality {Pick(s, "speed", "balanced", "quality")} -bf 0 " +
            $"-profile:v {s.Profile} {(s.RateControl == "vbr" ? "-rc vbr_peak" : "-rc cbr")} {Rate(s)}"),
        new("qsv", "Intel Quick Sync", s =>
            $"-vf hwmap=derive_device=qsv,format=qsv -c:v h264_qsv -preset {Pick(s, "veryfast", "medium", "slower")} " +
            $"-look_ahead 0 -profile:v {s.Profile} {Rate(s)}"),
        // Na CPU a imagem precisa sair da GPU (hwdownload); por isso a resolução de saída pesa muito aqui.
        new("x264", "CPU (x264)", s =>
            $"-vf hwdownload,format=bgra,format=yuv420p -c:v libx264 -preset {Pick(s, "ultrafast", "veryfast", "faster")} " +
            $"-tune zerolatency -profile:v {s.Profile} {Rate(s)}{(s.RateControl == "cbr" ? " -x264-params nal-hrd=cbr" : "")}"),
    ];

    private static string Pick(StreamSettings s, string speed, string balanced, string quality) =>
        s.Preset switch { "speed" => speed, "quality" => quality, _ => balanced };

    private static string Rate(StreamSettings s)
    {
        var b = s.BitrateKbps;
        return s.RateControl == "vbr"
            ? $"-b:v {b}k -maxrate {b * 3 / 2}k -bufsize {b}k"
            : $"-b:v {b}k -maxrate {b}k -bufsize {b / 2}k";
    }

    private readonly string _ffmpeg;
    private Process? _process;
    private volatile bool _stopping;

    /// <summary>Um quadro H.264 completo (Annex B) e se ele é quadro-chave.</summary>
    public event Action<byte[], bool>? Frame;

    /// <summary>O FFmpeg parou sozinho (ex.: a janela foi fechada).</summary>
    public event Action<string>? Failed;

    public string? EncoderLabel { get; private set; }
    public string? OutputSize { get; private set; }

    public VideoCapture(string ffmpeg) => _ffmpeg = ffmpeg;

    public async Task StartAsync(StreamSettings s, CaptureSource source, CancellationToken token)
    {
        // "auto" tenta as GPUs (NVIDIA, AMD, Intel) e usa a CPU como último recurso.
        var candidates = s.Encoder == "auto" ? Encoders : Encoders.Where(e => e.Id == s.Encoder).ToArray();
        if (candidates.Length == 0) throw new ArgumentException("Codificador desconhecido: " + s.Encoder);

        var errors = new StringBuilder();
        foreach (var enc in candidates)
        {
            token.ThrowIfCancellationRequested();
            var error = await TryStartAsync(BuildArgs(s, source, enc), token);
            if (error == null)
            {
                EncoderLabel = enc.Label;
                return;
            }
            errors.AppendLine($"{enc.Label}: {error}");
        }
        throw new InvalidOperationException("O codificador não funcionou.\n" + errors.ToString().Trim());
    }

    private string BuildArgs(StreamSettings s, CaptureSource source, EncoderDef enc)
    {
        var handle = source.Id[2..];
        var input = source.Kind == "window" ? $"hwnd={handle}" : $"hmonitor={handle}";

        // Resolução de saída: a GPU reduz mantendo a proporção (ex.: 3440×1440 → 1720×720).
        var size = "";
        OutputSize = $"{source.Width}×{source.Height}";
        if (s.Height > 0 && s.Height < source.Height)
        {
            var h = s.Height & ~1;
            var w = (int)Math.Round(source.Width * (double)h / source.Height / 2) * 2;
            size = $":width={w}:height={h}:resize_mode=scale_aspect";
            OutputSize = $"{w}×{h}";
        }

        var cursor = s.Cursor ? "1" : "0";
        var fpsMode = s.ConstantFps ? $"-fps_mode cfr -r {s.Fps}" : "-fps_mode passthrough";
        var keySec = Math.Max(0.5, s.KeyframeSeconds).ToString(CultureInfo.InvariantCulture);
        var gop = (int)Math.Round(s.Fps * Math.Max(0.5, s.KeyframeSeconds));

        return "-hide_banner -loglevel error -nostats " +
               $"-f lavfi -i gfxcapture={input}:max_framerate={s.Fps}:capture_cursor={cursor}{size} " +
               $"{fpsMode} {enc.Args(s)} -g {gop} -force_key_frames expr:gte(t,n_forced*{keySec}) " +
               "-bsf:v h264_metadata=aud=insert -flush_packets 1 -f h264 pipe:1";
    }

    /// <summary>Inicia o FFmpeg e espera o primeiro quadro. Retorna null se deu certo ou a mensagem de erro.</summary>
    private async Task<string?> TryStartAsync(string args, CancellationToken token)
    {
        var psi = new ProcessStartInfo(_ffmpeg, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        var process = Process.Start(psi)!;
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopping = false;
        _process = process;
        _ = Task.Run(() => ReadFrames(process, firstFrame, stderr));

        var winner = await Task.WhenAny(firstFrame.Task, process.WaitForExitAsync(token), Task.Delay(TimeSpan.FromSeconds(8), token));
        if (winner == firstFrame.Task) return null;

        Kill(process);
        _process = null;
        lock (stderr)
            return stderr.Length > 0 ? stderr.ToString().Trim() : "não produziu nenhum quadro";
    }

    private void ReadFrames(Process process, TaskCompletionSource firstFrame, StringBuilder stderr)
    {
        var stream = process.StandardOutput.BaseStream;
        var buf = new byte[4 * 1024 * 1024];
        var len = 0;
        var auStart = -1;   // início do quadro atual dentro de buf
        var scanFrom = 0;

        try
        {
            while (true)
            {
                if (len == buf.Length)
                {
                    // Quadro maior que o buffer (não deve acontecer): cresce.
                    Array.Resize(ref buf, buf.Length * 2);
                }
                var read = stream.Read(buf, len, buf.Length - len);
                if (read <= 0) break;
                len += read;

                // Procura delimitadores de quadro: 00 00 01 09 (NAL tipo 9 = AUD).
                for (var i = Math.Max(scanFrom, 0); i + 3 < len; i++)
                {
                    if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 1 || (buf[i + 3] & 0x1F) != 9) continue;
                    var start = i > 0 && buf[i - 1] == 0 ? i - 1 : i;
                    if (auStart >= 0 && start > auStart)
                    {
                        var frame = buf.AsSpan(auStart, start - auStart).ToArray();
                        Frame?.Invoke(frame, IsKeyframe(frame));
                        firstFrame.TrySetResult();
                    }
                    auStart = start;
                    i += 3;
                }
                scanFrom = Math.Max(0, len - 3);

                // Descarta o que já foi entregue.
                if (auStart > 0)
                {
                    Buffer.BlockCopy(buf, auStart, buf, 0, len - auStart);
                    len -= auStart;
                    scanFrom -= auStart;
                    auStart = 0;
                }
            }
        }
        catch (Exception) when (_stopping)
        {
        }
        catch (Exception ex)
        {
            Failed?.Invoke("Erro lendo o vídeo: " + ex.Message);
            return;
        }

        if (!_stopping && firstFrame.Task.IsCompleted)
        {
            string err;
            lock (stderr) err = stderr.ToString().Trim();
            Failed?.Invoke(string.IsNullOrEmpty(err) ? "A janela ou monitor deixou de estar disponível." : err);
        }
    }

    /// <summary>Quadro-chave = contém um NAL IDR (tipo 5).</summary>
    private static bool IsKeyframe(byte[] au)
    {
        for (var i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1)
            {
                if ((au[i + 3] & 0x1F) == 5) return true;
                i += 2;
            }
        }
        return false;
    }

    public void Stop()
    {
        _stopping = true;
        var p = _process;
        _process = null;
        if (p == null) return;
        try
        {
            // "q" pede ao FFmpeg para encerrar limpo; se não sair logo, mata.
            p.StandardInput.Write('q');
            p.StandardInput.Flush();
            if (!p.WaitForExit(1500)) Kill(p);
        }
        catch
        {
            Kill(p);
        }
        p.Dispose();
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose() => Stop();
}
