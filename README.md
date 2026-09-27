<p align="center">
  <img src="GameStream/Assets/app-256.png" width="96" alt="GameStream">
</p>

<h1 align="center">GameStream</h1>

<p align="center">
  Transmita a tela do seu jogo, com som, para amigos pela internet.<br>
  Eles só abrem um link no navegador. Não precisa de conta, de servidor nem de abrir portas.
</p>

<p align="center">
  <a href="https://github.com/renatoacj/gamestream/releases/latest"><img src="https://img.shields.io/github/v/release/renatoacj/gamestream?label=download&color=7c5cff" alt="Download"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/licen%C3%A7a-MIT-green" alt="Licença MIT"></a>
</p>

---

## Recursos

- **Link público gratuito e automático**: o app abre um [Cloudflare Quick Tunnel](https://try.cloudflare.com) e mostra um link `https://….trycloudflare.com`.
- **Captura de uma janela ou de um monitor** com o Windows Graphics Capture (via FFmpeg `gfxcapture`). A captura e a mudança de resolução acontecem na GPU.
- **Vídeo H.264 com baixa latência**, codificado por NVIDIA NVENC, AMD AMF, Intel Quick Sync ou CPU (x264), à sua escolha.
- **Áudio só da janela escolhida** (process loopback do Windows) ou de todo o PC, em Opus estéreo.
- **Configurações no estilo OBS**: resolução de saída, FPS constante, CBR/VBR, bitrate, preset, intervalo de quadro-chave, perfil H.264 e bitrate do áudio, além de predefinições prontas.
- **Não depende de P2P**: tudo passa pelo link HTTPS, então funciona em qualquer rede (4G, redes com NAT, etc.).
- **Um amigo com internet lenta não atrasa os outros**: cada um tem sua fila, e quem atrasa pula para o quadro-chave mais recente.
- **Seguro por padrão**: o servidor só escuta em `localhost`, e o link tem uma chave aleatória nova a cada execução.

## Instalar

**[⬇ Baixar o instalador (última versão)](https://github.com/renatoacj/gamestream/releases/latest)**

Baixe o `GameStream-Setup-x.y.z.exe` e execute. Não precisa ser administrador nem instalar o .NET, porque ele já vem embutido.

- **Requisitos:** Windows 10 1903+ ou Windows 11, 64 bits. O "áudio só da janela" exige Windows 11.
- **Aviso do Windows:** como o instalador não tem assinatura digital, o Windows pode mostrar "O Windows protegeu o computador". Clique em **Mais informações → Executar assim mesmo**.
- **WebView2:** se o computador não tiver o WebView2 Runtime, o instalador baixa e instala a versão oficial da Microsoft.

Para gerar o instalador (requer [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```bash
powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1
```

## Como usar

1. Abra o **GameStream**. Na primeira vez ele baixa o `cloudflared` e o FFmpeg oficiais (~200 MB).
2. Escolha a **janela do jogo** na lista e ajuste as configurações, ou use uma **predefinição**.
3. Clique em **▶ Iniciar transmissão**.
4. Copie o link da lateral e mande para os amigos. Eles devem abrir no Chrome, Edge ou Safari atualizado.
5. Cada amigo clica em **🔊 Ativar som**, porque o navegador exige um clique para tocar áudio.

> Dica: se travar, use **720p**, **30 fps** e **FPS constante**. No modo CPU, a resolução é o que mais pesa.

## Compilar

Requisitos: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) e o WebView2 Runtime (já vem no Windows 11).

```bash
dotnet publish GameStream -c Release -r win-x64 --self-contained false -o publish
```

O executável fica em `publish/GameStream.exe`. Se você colocar um `cloudflared.exe` ou um `ffmpeg.exe` na mesma pasta, o app usa esses em vez de baixar. O FFmpeg precisa ser recente (8.0 ou mais novo), com o filtro `gfxcapture`.

Para recriar o ícone: `powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1`.

## Como funciona

```
┌──────────────────────────── GameStream.exe ─────────────────────────────┐
│  ffmpeg: gfxcapture (janela/monitor, GPU) → NVENC/AMF/QSV/x264 → H.264  │
│  WASAPI process loopback (só o jogo) → Opus (Concentus)                 │
│        └────────────► Kestrel (localhost:8790): repassa p/ cada amigo   │
│  WebView2 (host.html): interface e prévia                               │
└───────────────────────────────────────────────┬─────────────────────────┘
                                                │ cloudflared (Quick Tunnel)
                                                ▼
         https://xxxx.trycloudflare.com/?k=…  →  navegador do amigo
                                                 (WebCodecs → canvas)
```

| Arquivo | Função |
|---|---|
| [`GameStream/MainWindow.xaml.cs`](GameStream/MainWindow.xaml.cs) | Janela, WebView2 e inicialização |
| [`GameStream/StreamServer.cs`](GameStream/StreamServer.cs) | Servidor HTTP, API de controle e repasse dos pacotes de mídia |
| [`GameStream/Broadcaster.cs`](GameStream/Broadcaster.cs) | Junta a captura de vídeo e de áudio e envia para o servidor |
| [`GameStream/VideoCapture.cs`](GameStream/VideoCapture.cs) | FFmpeg: captura, escala e codificação H.264 |
| [`GameStream/AudioCapture.cs`](GameStream/AudioCapture.cs) / [`ProcessLoopback.cs`](GameStream/ProcessLoopback.cs) | Áudio da janela ou do PC e codificação Opus |
| [`GameStream/CaptureSources.cs`](GameStream/CaptureSources.cs) | Lista de janelas e monitores |
| [`GameStream/PublicTunnel.cs`](GameStream/PublicTunnel.cs) | Baixa e executa o `cloudflared` e lê o link público |
| [`GameStream/wwwroot/host.js`](GameStream/wwwroot/host.js) | Interface do app (configurações e status) |
| [`GameStream/wwwroot/viewer.js`](GameStream/wwwroot/viewer.js) | Decodificação e reprodução (navegador do amigo) |

**Protocolo dos pacotes** (binário, via WebSocket): `[tipo u8: 1=vídeo 2=áudio][flags u8: bit0=quadro-chave][timestamp f64 LE µs][dados]`.

## Limitações

- **Upload**: cada amigo recebe uma cópia do vídeo, então o upload necessário é o bitrate × o número de amigos. Com 6 Mbps e 3 amigos, são cerca de 18 Mbps.
- **Quick Tunnel**: a Cloudflare oferece esse serviço de graça e sem garantia de disponibilidade (SLA). O link muda a cada vez que o app abre.
- **Navegador do amigo**: precisa ter WebCodecs. Chrome, Edge e Safari atualizados funcionam, e o Firefox recente também deve funcionar.

## Licença

[MIT](LICENSE)
