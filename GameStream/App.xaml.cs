using System.Threading;
using System.Windows;

namespace GameStream;

public partial class App : Application
{
    // Uma cópia do app por usuário: abrir de novo (atalho, menu Iniciar) só traz a janela que já está aberta,
    // inclusive quando ela está escondida na bandeja.
#if DEBUG
    private const string InstanceName = "GameStream-dev";
#else
    private const string InstanceName = "GameStream";
#endif
    private Mutex? _instance;
    private EventWaitHandle? _showRequest;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var startup = e.Args.Contains(AppSettings.StartupArgument);

#if !DEBUG   // em desenvolvimento várias cópias rodam lado a lado nos testes
        _instance = new Mutex(true, $@"Local\{InstanceName}-instance", out var first);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceName}-show");
        if (!first)
        {
            if (!startup) _showRequest.Set();   // pede para a cópia aberta aparecer
            Shutdown();
            return;
        }
#endif

        ProcessTuning.TuneSelf();
        var window = new MainWindow(startHidden: startup);
        MainWindow = window;
        if (_showRequest != null)
        {
            ThreadPool.RegisterWaitForSingleObject(_showRequest,
                (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Desligando o Windows ou o instalador atualizando o app: fecha de verdade, sem ir para a bandeja.
        (MainWindow as MainWindow)?.PrepareExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRequest?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
