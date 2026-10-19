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
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _exiting;            // fechar de verdade (Sair, desligar o Windows), não ir para a bandeja
    private bool _trayHintShown;
    private bool _startHidden;        // aberto com o Windows: começa só na bandeja

    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();
        _startHidden = startHidden;
        if (startHidden)
        {
            // A janela precisa existir para o WebView2 iniciar; fica minimizada e fora da barra até ser escondida.
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += OnLoaded;
        CreateTray();
        Closing += (_, e) =>
        {
            if (_exiting || !AppSettings.Current.CloseToTray) return;
            // Fechar a janela só a esconde: transmissão e salas continuam funcionando.
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloonTip(4000, "O GameStream continua aberto",
                    "Ele está aqui na bandeja. Para sair, clique com o botão direito no ícone.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        };
        Closed += async (_, _) =>
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _broadcaster?.Dispose();
            _tunnel?.Dispose();
            if (_server != null) await _server.DisposeAsync();
        };
    }

    // ---------- Bandeja do sistema ----------

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir o GameStream", null, (_, _) => ShowFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitApp());
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "GameStream",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray();
        };
    }

    /// <summary>Traz a janela de volta (clique no ícone da bandeja ou o app aberto de novo).</summary>
    public void ShowFromTray()
    {
        _startHidden = false;
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Traz para a frente mesmo quando outro programa (o jogo) está em primeiro plano.
        Topmost = true;
        Topmost = false;
    }

    /// <summary>A próxima tentativa de fechar encerra o app de verdade.</summary>
    public void PrepareExit() => _exiting = true;

    private void ExitApp()
    {
        _exiting = true;
        Close();
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

            // A sala (vídeo e som das telas dos outros) roda dentro do WebView2. Enquanto a pessoa joga, a janela
            // do app fica atrás do jogo ou minimizada, e por padrão o Chromium trata isso como "aba em segundo
            // plano": reduz a prioridade da página e o som fica picotado. Estas opções desligam essa economia.
            var browserArgs = new List<string> { "--autoplay-policy=no-user-gesture-required" };
            // GAMESTREAM_NO_BG_FIX=1 mantém a economia ligada (só para comparar o antes e o depois em testes).
            if (Environment.GetEnvironmentVariable("GAMESTREAM_NO_BG_FIX") != "1")
            {
                browserArgs.AddRange([
                    "--disable-background-timer-throttling",
                    "--disable-renderer-backgrounding",
                    "--disable-backgrounding-occluded-windows",
                    "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling",
                ]);
            }
#if DEBUG
            // Só em desenvolvimento: porta de depuração para inspecionar a página por script.
            if (int.TryParse(Environment.GetEnvironmentVariable("GAMESTREAM_DEBUG_PORT"), out var debugPort))
                browserArgs.AddRange([$"--remote-debugging-port={debugPort}", "--remote-allow-origins=*"]);
#endif
            var options = new CoreWebView2EnvironmentOptions(string.Join(' ', browserArgs));
#if DEBUG
            // Pasta própria nas compilações de desenvolvimento (uma por porta de depuração), para rodarem
            // ao lado do app instalado e entre si com opções diferentes.
            var dataDir = Path.Combine(Tools.DataDir, "WebView2-dev" + Environment.GetEnvironmentVariable("GAMESTREAM_DEBUG_PORT"));
#else
            var dataDir = Path.Combine(Tools.DataDir, "WebView2");
#endif
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
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
            if (_startHidden)
            {
                // Aberto com o Windows: fica só na bandeja (ou minimizado, se a bandeja estiver desligada).
                if (AppSettings.Current.CloseToTray) Hide();
                ShowInTaskbar = true;
            }
        }
        catch (Exception ex)
        {
            StartupError.Text = "Falha ao iniciar: " + ex.Message +
                "\n\nVerifique se as portas a partir de " + PreferredPort + " estão livres e se o WebView2 Runtime está instalado.";
            StartupError.Visibility = Visibility.Visible;
        }
    }
}
