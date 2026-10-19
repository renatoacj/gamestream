using Concentus;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameStream;

/// <summary>
/// Toca, no próprio GameStream.exe, o som da tela escolhida na sala.
///
/// Dentro do app a sala é uma página do WebView2, e o Chromium toca áudio num processo dele
/// (msedgewebview2.exe). Programas que separam o som por aplicativo (MixLine, Voicemeeter, o mixer
/// do Windows) não achavam o som do GameStream. Por isso, no app, a página só mostra o vídeo e avisa
/// qual tela está tocando som; o áudio (Opus) é decodificado e tocado aqui.
/// </summary>
public sealed class RoomAudioPlayer : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int HeaderSize = 12;              // mesmo cabeçalho dos pacotes da sala
    private const int MaxFrame = 5760;              // 120 ms a 48 kHz, o maior pacote Opus
    private static readonly TimeSpan StartBuffer = TimeSpan.FromMilliseconds(120);   // folga contra a variação da rede
    private static readonly TimeSpan MaxBuffer = TimeSpan.FromMilliseconds(450);     // acima disso, joga fora o atraso

    private readonly object _lock = new();
    private readonly float[] _pcm = new float[MaxFrame * Channels];
    private IOpusDecoder? _decoder;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private bool _playing;
    private volatile bool _outputFailed;
    private float _volume = 1;
    private bool _muted;
    private bool _decodeErrorLogged;

    /// <summary>Número da tela que está tocando som, ou null.</summary>
    public ushort? Stream { get; private set; }

    /// <param name="volume">0 a 4 (até 400%, para quem transmite com o som baixo).</param>
    public void Listen(ushort? stream, float volume, bool muted)
    {
        lock (_lock)
        {
            _volume = Math.Clamp(volume, 0, 4);
            _muted = muted;
            if (stream == Stream) return;
            Stream = stream;
            StopOutput();
            _decoder = stream == null ? null : OpusCodecFactory.CreateDecoder(SampleRate, Channels);
            _decodeErrorLogged = false;
        }
    }

    /// <summary>Um pacote da sala ([tipo][flags][tela u16][timestamp f64][dados]); só o áudio da tela escolhida é tocado.</summary>
    public void OnPacket(byte[] packet)
    {
        if (packet.Length <= HeaderSize || packet[0] != 2) return;
        var no = BitConverter.ToUInt16(packet, 2);
        lock (_lock)
        {
            if (no != Stream || _decoder == null) return;
            int frames;
            try
            {
                frames = _decoder.Decode(packet.AsSpan(HeaderSize), _pcm, MaxFrame);
            }
            catch (Exception ex)
            {
                if (!_decodeErrorLogged) Tools.Log("Som da sala: pacote de áudio inválido: " + ex.Message);
                _decodeErrorLogged = true;
                return;
            }
            if (frames <= 0) return;

            var gain = _muted ? 0f : _volume;
            var count = frames * Channels;
            var bytes = new byte[count * sizeof(float)];
            for (var i = 0; i < count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float)), SoftClip(_pcm[i] * gain));

            EnsureOutput();
            if (_buffer == null || _output == null) return;
            if (_buffer.BufferedDuration > MaxBuffer) _buffer.ClearBuffer();   // a rede engasgou e chegou tudo junto
            _buffer.AddSamples(bytes, 0, bytes.Length);
            if (!_playing && _buffer.BufferedDuration >= StartBuffer)
            {
                _output.Play();
                _playing = true;
            }
        }
    }

    /// <summary>Volume acima de 100% sem distorcer: o que passa de 0,9 é arredondado em vez de cortado.</summary>
    private static float SoftClip(float x)
    {
        var a = MathF.Abs(x);
        return a <= 0.9f ? x : MathF.CopySign(0.9f + 0.1f * MathF.Tanh((a - 0.9f) / 0.1f), x);
    }

    private void EnsureOutput()
    {
        if (_outputFailed) StopOutput();   // o dispositivo sumiu ou mudou: abre de novo na saída padrão atual
        if (_output != null) return;
        try
        {
            _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            _output = new WasapiOut(AudioClientShareMode.Shared, true, 50);
            _output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception == null) return;
                Tools.Log("Som da sala: a saída de som parou: " + e.Exception.Message);
                _outputFailed = true;
            };
            _output.Init(_buffer);
            _playing = false;
        }
        catch (Exception ex)
        {
            Tools.Log("Som da sala: não foi possível abrir a saída de som: " + ex.Message);
            StopOutput();
        }
    }

    private void StopOutput()
    {
        var output = _output;
        _output = null;
        _buffer = null;
        _playing = false;
        _outputFailed = false;
        if (output == null) return;
        try { output.Stop(); } catch { /* já parado */ }
        output.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Stream = null;
            _decoder = null;
            StopOutput();
        }
    }
}
