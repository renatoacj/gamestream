using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace GameStream;

public partial class MainWindow : Window
{
    private const int PreferredPort = 8790;
    private StreamServer? _server;
    private PublicTunnel? _tunnel;
    private Broadcaster? _broadcaster;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += async (_, _) =>
        {
            _broadcaster?.Dispose();
            _tunnel?.Dispose();
            if (_server != null) await _server.DisposeAsync();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _server = await StreamServer.StartOnFreePortAsync(PreferredPort, Path.Combine(AppContext.BaseDirectory, "wwwroot"));
            var port = _server.Port;

            _tunnel = new PublicTunnel(port);
            _server.Tunnel = _tunnel;
            _ = _tunnel.RunAsync();

            // Captura e codificação rodam aqui no C# (FFmpeg + WASAPI); o WebView2 é só a interface.
            _broadcaster = new Broadcaster(_server);
            _server.Broadcaster = _broadcaster;
            _ = _broadcaster.PrepareAsync();

            var dataDir = Path.Combine(Tools.DataDir, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            // A janela do app só mostra a própria interface: qualquer tentativa de navegar
            // para outro endereço ou abrir janelas novas é bloqueada.
            var appOrigin = $"http://localhost:{port}/";
            core.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith(appOrigin, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            core.FrameNavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith(appOrigin, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;

            // localhost é "contexto seguro", necessário para o WebCodecs da prévia.
            core.Navigate($"http://localhost:{port}/host.html?t={_server.HostToken}");
        }
        catch (Exception ex)
        {
            StartupError.Text = "Falha ao iniciar: " + ex.Message +
                "\n\nVerifique se as portas a partir de " + PreferredPort + " estão livres e se o WebView2 Runtime está instalado.";
            StartupError.Visibility = Visibility.Visible;
        }
    }
}
