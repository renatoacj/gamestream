using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += OnLoaded;
        Closed += async (_, _) =>
        {
            _broadcaster?.Dispose();
            _tunnel?.Dispose();
            if (_server != null) await _server.DisposeAsync();
        };
    }

    /// <summary>Barra de título escura, na cor do app (em vez da barra branca padrão).</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        // Windows 11: cor exata do fundo do app (#0f1115) e texto claro. No Windows 10 só o modo escuro acima vale.
        var caption = 0x0015110F;   // COLORREF = 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        var text = 0x00EEE8E6;
        DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
                // Além da própria interface, só a sala em que o usuário entrou (endereço validado em RoomClient).
                var joined = _server.Joined?.Origin;
                var allowed = args.Uri.StartsWith(appOrigin, StringComparison.OrdinalIgnoreCase) ||
                              (joined != null && args.Uri.StartsWith(joined + "/", StringComparison.OrdinalIgnoreCase));
                if (!allowed) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;

            // localhost é "contexto seguro", necessário para o WebCodecs da sala.
            core.Navigate($"http://localhost:{port}/host.html?t={_server.HostToken}");
#if DEBUG
            // Só em compilações de desenvolvimento: permite testar a API de controle por script.
            Tools.Log($"DEBUG controle: http://localhost:{port}/api/status?t={_server.HostToken}");
#endif
        }
        catch (Exception ex)
        {
            StartupError.Text = "Falha ao iniciar: " + ex.Message +
                "\n\nVerifique se as portas a partir de " + PreferredPort + " estão livres e se o WebView2 Runtime está instalado.";
            StartupError.Visibility = Visibility.Visible;
        }
    }
}
