using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GameStream;

/// <summary>
/// Captura áudio e codifica em Opus estéreo 48 kHz, em pacotes de 20 ms, prontos
/// para o AudioDecoder do navegador. Duas fontes:
/// só um programa (process loopback) ou todo o som do PC (WASAPI loopback).
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int FrameSamples = 960;              // 20 ms a 48 kHz
    private const int FrameValues = FrameSamples * Channels;

    private IOpusEncoder? _encoder;
    private float _gain = 1f;
    private float _peak;   // maior amostra desde a última leitura (para o medidor)
    private readonly float[] _frame = new float[FrameValues];
    private int _frameFill;
    private readonly byte[] _packet = new byte[4000];
    private readonly object _lock = new();

    // Fonte "processo"
    private ProcessLoopback? _process;

    // Fonte "sistema"
    private WasapiLoopbackCapture? _system;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _pipeline;
    private readonly float[] _readBuf = new float[FrameValues * 4];

    /// <summary>Um pacote Opus de 20 ms.</summary>
    public event Action<byte[]>? Packet;

    /// <summary>Ganho em dB aplicado antes de codificar.</summary>
    public int GainDb
    {
        set => _gain = MathF.Pow(10, Math.Clamp(value, -20, 24) / 20f);
    }

    /// <summary>Pico (0..1) desde a última chamada; usado pelo medidor de nível.</summary>
    public float TakePeak()
    {
        lock (_lock)
        {
            var p = _peak;
            _peak = 0;
            return p;
        }
    }

    /// <summary>Só o som do processo <paramref name="processId"/> (e dos filhos dele).</summary>
    public async Task StartProcessAsync(int processId, int kbps)
    {
        CreateEncoder(kbps);
        _process = new ProcessLoopback();
        _process.Samples += Feed;
        await _process.StartAsync(processId);
    }

    /// <summary>Todo o som que está tocando no PC.</summary>
    public void StartSystem(int kbps)
    {
        CreateEncoder(kbps);
        _system = new WasapiLoopbackCapture();
        var format = _system.WaveFormat;
        _buffer = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1),
            ReadFully = false,
        };

        // Converte o formato do dispositivo (ex.: 44,1 kHz, 5.1) para 48 kHz estéreo.
        ISampleProvider sp = _buffer.ToSampleProvider();
        if (format.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (format.Channels > 2) sp = new FirstTwoChannels(sp);
        if (format.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
        _pipeline = sp;

        _system.DataAvailable += (_, e) =>
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            int read;
            while ((read = _pipeline.Read(_readBuf, 0, _readBuf.Length)) > 0) Feed(_readBuf, read);
        };
        _system.StartRecording();
    }

    private void CreateEncoder(int kbps)
    {
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = Math.Clamp(kbps, 32, 510) * 1000;
    }

    /// <summary>Junta as amostras em quadros de 20 ms e codifica.</summary>
    private void Feed(float[] samples, int count)
    {
        lock (_lock)
        {
            var offset = 0;
            while (offset < count)
            {
                var take = Math.Min(count - offset, FrameValues - _frameFill);
                for (var i = 0; i < take; i++)
                {
                    var v = Limit(samples[offset + i] * _gain);
                    _frame[_frameFill + i] = v;
                    var a = MathF.Abs(v);
                    if (a > _peak) _peak = a;
                }
                _frameFill += take;
                offset += take;
                if (_frameFill == FrameValues)
                {
                    _frameFill = 0;
                    var len = _encoder!.Encode(_frame, FrameSamples, _packet, _packet.Length);
                    if (len > 0) Packet?.Invoke(_packet.AsSpan(0, len).ToArray());
                }
            }
        }
    }

    /// <summary>Limitador suave: abaixo de 0,8 não mexe; acima, comprime em direção a 1,0 sem estourar.</summary>
    private static float Limit(float x)
    {
        const float knee = 0.8f;
        var a = MathF.Abs(x);
        if (a <= knee) return x;
        var y = knee + (1 - knee) * MathF.Tanh((a - knee) / (1 - knee));
        return MathF.CopySign(y, x);
    }

    public void Stop()
    {
        _process?.Dispose();
        _process = null;

        var system = _system;
        _system = null;
        if (system != null)
        {
            try { system.StopRecording(); } catch { }
            system.Dispose();
        }
    }

    public void Dispose() => Stop();

    /// <summary>Mantém só os canais frontais esquerdo/direito de um áudio multicanal.</summary>
    private sealed class FirstTwoChannels(ISampleProvider source) : ISampleProvider
    {
        private readonly int _inChannels = source.WaveFormat.Channels;
        private float[] _buf = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            var need = frames * _inChannels;
            if (_buf.Length < need) _buf = new float[need];
            var got = source.Read(_buf, 0, need) / _inChannels;
            for (var f = 0; f < got; f++)
            {
                buffer[offset + f * 2] = _buf[f * _inChannels];
                buffer[offset + f * 2 + 1] = _buf[f * _inChannels + 1];
            }
            return got * 2;
        }
    }
}
