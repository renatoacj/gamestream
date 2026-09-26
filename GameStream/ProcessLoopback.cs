using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace GameStream;

/// <summary>
/// Captura só o áudio de um processo (e dos processos filhos) com a API de
/// "process loopback" do Windows (Windows 10 build 20348+ / Windows 11).
/// Entrega amostras float 48 kHz estéreo.
/// </summary>
public sealed class ProcessLoopback : IDisposable
{
    private AudioClient? _client;
    private Thread? _thread;
    private volatile bool _running;
    private EventWaitHandle? _event;

    /// <summary>Amostras intercaladas (L, R, L, R…) e quantos valores são válidos.</summary>
    public event Action<float[], int>? Samples;

    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public async Task StartAsync(int processId)
    {
        _client = await ActivateAsync(processId);
        _client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
            200 * 10_000, 0, Format, Guid.Empty);

        _event = new EventWaitHandle(false, EventResetMode.AutoReset);
        _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        _client.Start();

        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ProcessLoopback" };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        var capture = _client!.AudioCaptureClient;
        var buffer = new float[48000 * 2];
        while (_running)
        {
            _event!.WaitOne(100);
            int packet;
            while (_running && (packet = capture.GetNextPacketSize()) > 0)
            {
                var data = capture.GetBuffer(out var frames, out var flags);
                var count = frames * 2;
                if (count > buffer.Length) buffer = new float[count];
                if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buffer, 0, count);
                else Marshal.Copy(data, buffer, 0, count);
                capture.ReleaseBuffer(frames);
                Samples?.Invoke(buffer, count);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        try { _client?.Stop(); } catch { }
        _client?.Dispose();
        _client = null;
        _event?.Dispose();
    }

    // ---------- Ativação (ActivateAudioInterfaceAsync) ----------

    private const string VirtualDeviceProcessLoopback = "VAD\\Process_Loopback";
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    private static Task<AudioClient> ActivateAsync(int processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK, { TargetProcessId, INCLUDE_TARGET_PROCESS_TREE } }
        var parameters = Marshal.AllocHGlobal(12);
        Marshal.WriteInt32(parameters, 0, 1);
        Marshal.WriteInt32(parameters, 4, processId);
        Marshal.WriteInt32(parameters, 8, 0);

        // PROPVARIANT { vt = VT_BLOB, blob = { cbSize = 12, pBlobData = parameters } }
        var propVariant = Marshal.AllocHGlobal(24);
        for (var i = 0; i < 24; i += 8) Marshal.WriteInt64(propVariant, i, 0);
        Marshal.WriteInt16(propVariant, 0, 65);
        Marshal.WriteInt32(propVariant, 8, 12);
        Marshal.WriteIntPtr(propVariant, 16, parameters);

        var handler = new CompletionHandler(() =>
        {
            Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(parameters);
        });

        // Chamado numa thread MTA do pool, como a API exige.
        return Task.Run(() =>
        {
            ActivateAudioInterfaceAsync(VirtualDeviceProcessLoopback, IID_IAudioClient, propVariant, handler, out _);
            return handler.Task;
        });
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>IAgileObject: a API chama o handler de outra thread.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    private sealed class CompletionHandler(Action cleanup) : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<AudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AudioClient> Task => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out var hr, out var obj);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                _tcs.TrySetResult(new AudioClient((IAudioClient)obj));
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            finally
            {
                cleanup();
            }
        }
    }
}
