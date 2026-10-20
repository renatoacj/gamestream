using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GameStream;

/// <summary>
/// Liga a captura de vídeo (FFmpeg) e de áudio (WASAPI) ao servidor, montando os
/// pacotes do protocolo e medindo quadros/bitrate para o painel.
/// </summary>
public sealed class Broadcaster : IDisposable
{
    // Para onde a transmissão vai: a sala local ou a sala de outra pessoa (quando o app "entra" numa sala).
    private IRoomSink _server;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private VideoCapture? _video;
    private AudioCapture? _audio;
    private CancellationTokenSource? _thumbnails;

    private double _nextVideoTs;           // horário "ideal" do próximo quadro (µs)
    private double _frameIntervalMicros;   // 1/fps em µs; 0 = FPS variável

    private long _frames, _bytes;
    private long _lastStatsTicks;
    private double _fps, _kbps;

    public string State { get; private set; } = "offline";   // offline | starting | live | error
    public string? Message { get; private set; }
    public string? Encoder => _video?.EncoderLabel;
    public string? OutputSize => _video?.OutputSize;
    public string? AudioLabel { get; private set; }
    public string? SourceName { get; private set; }

    public Broadcaster(IRoomSink sink) => _server = sink;

    /// <summary>Troca o destino da transmissão (parando a que estiver no ar).</summary>
    public async Task SetSinkAsync(IRoomSink sink)
    {
        await StopAsync();
        _server = sink;
    }

    /// <summary>Encerra a transmissão mostrando um motivo (ex.: a sala recusou o compartilhamento).</summary>
    public async Task StopWithMessageAsync(string reason)
    {
        await _lock.WaitAsync();
        try
        {
            StopCore();
            State = "error";
            Message = reason;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Pico do áudio em dBFS desde a última consulta (medidor), ou null sem áudio.</summary>
    public double? TakeAudioLevelDb()
    {
        var audio = _audio;
        if (audio == null) return null;
        var peak = audio.TakePeak();
        return peak <= 0.00001f ? -100 : Math.Round(20 * Math.Log10(peak), 1);
    }

    /// <summary>Ganho que o modo automático está aplicando agora (dB), ou null se o ganho é manual ou não há áudio.</summary>
    public double? AutoGainDb => _audio is { AutoGain: true } a ? Math.Round(a.CurrentAutoGainDb) : null;

    /// <summary>Baixa o FFmpeg já na abertura do app (só na primeira vez), para o "Iniciar" ser rápido.</summary>
    public async Task PrepareAsync()
    {
        try
        {
            var ffmpeg = await Tools.EnsureFfmpegAsync(m => { if (State == "offline") Message = m; });
            if (State == "offline") Message = null;
            _ = VideoCapture.ProbeEncodersAsync(ffmpeg);   // já deixa pronto o teste dos codificadores
        }
        catch (Exception ex)
        {
            if (State == "offline") Message = "Falha ao baixar o FFmpeg: " + ex.Message;
        }
    }

    /// <param name="switching">
    /// Troca de janela/monitor durante a transmissão: a captura recomeça com a fonte nova, mas quem assiste
    /// não recebe "transmissão encerrada" — só vê a imagem mudar (o decodificador recomeça no próximo quadro-chave).
    /// </param>
    public async Task StartAsync(StreamSettings s, bool switching = false)
    {
        s = Sanitize(s);
        await _lock.WaitAsync();
        try
        {
            StopCore(publishOffline: !switching);
            State = "starting";
            Message = "Iniciando captura…";

            var source = CaptureSources.Find(s.Source)
                         ?? throw new InvalidOperationException("A janela ou monitor escolhido não existe mais. Atualize a lista.");
            SourceName = source.Name;

            var ffmpeg = await Tools.EnsureFfmpegAsync(m => Message = m);
            Message = "Iniciando captura…";
            var video = new VideoCapture(ffmpeg);
            video.Frame += OnVideoFrame;
            video.Failed += err =>
            {
                Tools.Log("A captura parou: " + err);
                State = "error";
                Message = "A captura parou: " + err;
                _thumbnails?.Cancel();
                _audio?.Stop();
                _server.PublishOffline();
            };

            var audioMode = s.AudioMode;
            if (audioMode == "window" && source.Kind != "window") audioMode = "system";   // monitor não tem "programa"

            _nextVideoTs = 0;
            _frameIntervalMicros = s.ConstantFps ? 1_000_000.0 / s.Fps : 0;

            // Config antes do primeiro quadro, para os espectadores prepararem o decodificador.
            _server.PublishConfig(BuildConfig(s.Profile, audioMode != "none"));
            await video.StartAsync(s, source, CancellationToken.None);
            _video = video;
            var notes = new List<string>();
            if (video.Warning != null) notes.Add(video.Warning);
            StartThumbnails(source.Id);

            AudioLabel = "Não";
            if (audioMode != "none")
            {
                try
                {
                    var audio = new AudioCapture { GainDb = s.AudioGainDb, AutoGain = s.AudioAutoGain };
                    audio.Packet += OnAudioPacket;
                    if (audioMode == "window")
                    {
                        await audio.StartProcessAsync(source.ProcessId, s.AudioKbps);
                        AudioLabel = "Só da janela";
                    }
                    else
                    {
                        audio.StartSystem(s.AudioKbps);
                        AudioLabel = "Todo o PC";
                    }
                    _audio = audio;
                }
                catch (Exception ex)
                {
                    // Sem áudio não impede a transmissão.
                    Tools.Log("Falha ao capturar áudio: " + ex);
                    notes.Add("Transmitindo sem áudio: " + ex.Message);
                    AudioLabel = "Não";
                    _server.PublishConfig(BuildConfig(s.Profile, false));
                }
            }

            State = "live";
            Message = notes.Count > 0 ? string.Join('\n', notes) : null;
        }
        catch (Exception ex)
        {
            Tools.Log("Falha ao iniciar a transmissão: " + ex.Message);
            StopCore();
            State = "error";
            Message = ex.Message;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            StopCore();
            State = "offline";
            Message = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Enquanto transmite, manda uma miniatura da tela para a sala a cada poucos segundos.</summary>
    private void StartThumbnails(string sourceId)
    {
        var cts = _thumbnails = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (await Thumbnails.GetAsync(sourceId) is { } jpeg && !cts.IsCancellationRequested)
                        _server.PublishThumbnail(jpeg);
                    await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Tools.Log("Miniatura: " + ex.Message); }
            }
        });
    }

    private void StopCore(bool publishOffline = true)
    {
        _thumbnails?.Cancel();
        _thumbnails = null;
        _video?.Stop();
        _video = null;
        _audio?.Stop();
        _audio = null;
        AudioLabel = null;
        if (publishOffline) _server.PublishOffline();
    }

    /// <summary>
    /// Só aceita valores conhecidos. Vários campos viram argumentos do FFmpeg, então nada
    /// que venha da interface chega à linha de comando sem passar por aqui.
    /// </summary>
    private static StreamSettings Sanitize(StreamSettings s) => s with
    {
        Height = s.Height <= 0 ? 0 : Math.Clamp(s.Height, 144, 4320),
        Fps = Math.Clamp(s.Fps, 10, 144),
        Encoder = s.Encoder is "nvenc" or "amf" or "qsv" or "x264" ? s.Encoder : "auto",
        RateControl = s.RateControl == "vbr" ? "vbr" : "cbr",
        BitrateKbps = Math.Clamp(s.BitrateKbps, 300, 100_000),
        Preset = s.Preset is "speed" or "quality" ? s.Preset : "balanced",
        KeyframeSeconds = double.IsFinite(s.KeyframeSeconds) ? Math.Clamp(s.KeyframeSeconds, 0.5, 10) : 1,
        Profile = s.Profile == "main" ? "main" : "high",
        AudioMode = s.AudioMode is "system" or "none" ? s.AudioMode : "window",
        AudioKbps = Math.Clamp(s.AudioKbps, 32, 510),
        AudioGainDb = Math.Clamp(s.AudioGainDb, 0, 24),
    };

    private static JsonObject BuildConfig(string profile, bool audio)
    {
        return new JsonObject
        {
            // Nível 5.2 cobre até 4K60; o decodificador aceita qualquer nível menor.
            ["video"] = new JsonObject { ["codec"] = profile == "main" ? "avc1.4d0034" : "avc1.640034" },
            ["audio"] = audio
                ? new JsonObject { ["codec"] = "opus", ["sampleRate"] = AudioCapture.SampleRate, ["numberOfChannels"] = AudioCapture.Channels }
                : null,
        };
    }

    private long NowMicros() => _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    private void OnVideoFrame(byte[] frame, bool key)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, frame.Length);
        _server.PublishMedia(StreamServer.BuildPacket(StreamServer.KindVideo, key, VideoTimestamp(), frame), isVideo: true, isKey: key);
    }

    /// <summary>
    /// Com FPS constante, os quadros saem do FFmpeg em pequenas rajadas (ele completa o ritmo
    /// repetindo quadros). Em vez do horário de chegada, cada quadro recebe o horário "ideal":
    /// o anterior + 1/fps. Uma correção suave acompanha o relógio real, para o vídeo não se
    /// afastar do áudio. O navegador exibe os quadros nesses horários, com ritmo uniforme.
    /// </summary>
    private long VideoTimestamp()
    {
        var now = NowMicros();
        if (_frameIntervalMicros <= 0) return now;   // FPS variável: horário de chegada

        if (_nextVideoTs == 0 || Math.Abs(now - _nextVideoTs) > 150_000)
            _nextVideoTs = now;                           // início ou atraso grande: ressincroniza
        else
            _nextVideoTs += (now - _nextVideoTs) / 32;   // puxa devagar para o relógio real

        var ts = (long)_nextVideoTs;
        _nextVideoTs += _frameIntervalMicros;
        return ts;
    }

    private void OnAudioPacket(byte[] opus) =>
        _server.PublishMedia(StreamServer.BuildPacket(StreamServer.KindAudio, true, NowMicros(), opus), isVideo: false, isKey: false);

    /// <summary>Quadros por segundo e kbps de vídeo desde a última consulta.</summary>
    public (double Fps, double Kbps) SampleStats()
    {
        lock (_clock)
        {
            // Consultas muito próximas repetem o último valor, para a média não oscilar.
            var now = _clock.ElapsedTicks;
            var elapsed = (double)(now - _lastStatsTicks) / Stopwatch.Frequency;
            if (elapsed >= 0.5)
            {
                _lastStatsTicks = now;
                _fps = Interlocked.Exchange(ref _frames, 0) / elapsed;
                _kbps = Interlocked.Exchange(ref _bytes, 0) * 8 / 1000.0 / elapsed;
            }
            return (_fps, _kbps);
        }
    }

    public void Dispose() => StopCore();
}
