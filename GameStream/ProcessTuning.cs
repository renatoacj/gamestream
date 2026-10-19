using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameStream;

/// <summary>
/// Mantém a captura no ritmo enquanto o jogo está em primeiro plano.
///
/// Com o jogo na frente (e o GameStream atrás ou só na bandeja), o Windows trata o app como "segundo plano":
/// pode colocá-lo em economia de energia (EcoQoS/modo de eficiência, que reduz a velocidade do processador
/// para ele) e o jogo leva a maior parte da placa de vídeo. A captura e a codificação atrasam e a transmissão
/// engasga — e fica lisa assim que o jogo sai da frente (Alt+Tab). Como o OBS, o app pede ao Windows para não
/// economizar com ele e para dar prioridade à captura, também na placa de vídeo.
/// </summary>
internal static class ProcessTuning
{
    /// <summary>O próprio app (áudio, envio pela rede).</summary>
    public static void TuneSelf()
    {
        using var me = Process.GetCurrentProcess();
        Tune(me, "GameStream");
    }

    /// <summary>O FFmpeg que captura e codifica a tela.</summary>
    public static void TuneCapture(Process process)
    {
        Tune(process, "captura");
        // A prioridade na placa de vídeo só vale depois que o FFmpeg abre a placa: espera um pouco.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
        {
            try
            {
                if (process.HasExited) return;
                var status = D3DKMTSetProcessSchedulingPriorityClass(process.Handle, D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
                Tools.Log(status == 0
                    ? "Prioridade da captura na placa de vídeo: alta."
                    : $"Prioridade da captura na placa de vídeo: não foi possível ajustar (0x{status:X8}).");
            }
            catch (Exception ex)
            {
                Tools.Log("Prioridade da captura na placa de vídeo: " + ex.Message);
            }
        }, TaskScheduler.Default);
    }

    private static void Tune(Process process, string label)
    {
        var problems = new List<string>();
        try { process.PriorityClass = ProcessPriorityClass.AboveNormal; }
        catch (Exception ex) { problems.Add("prioridade: " + ex.Message); }

        try
        {
            // Desliga a economia de energia (EcoQoS) para este processo, mesmo em segundo plano.
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0,
            };
            if (!SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                problems.Add($"economia de energia: erro {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            problems.Add("economia de energia: " + ex.Message);
        }

        Tools.Log(problems.Count == 0
            ? $"Prioridade da {label} ajustada (acima do normal, sem economia de energia)."
            : $"Prioridade da {label}: não foi possível ajustar tudo ({string.Join("; ", problems)}).");
    }

    private const int ProcessPowerThrottling = 4;   // PROCESS_INFORMATION_CLASS
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;
    private const int D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
}
