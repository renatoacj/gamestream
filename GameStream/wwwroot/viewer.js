'use strict';

// Recebe vídeo (H.264) e áudio (Opus) já codificados pelo WebSocket
// e decodifica com WebCodecs. Tudo passa pelo link HTTPS, sem P2P.

const params = new URLSearchParams(location.search);
const VIEW_KEY = params.get('k') || '';
// Modo prévia: a mesma página dentro da janela do app, sem som e sem controles.
const PREVIEW = params.get('preview') === '1';
const HOST_TOKEN = params.get('t') || '';   // só a prévia dentro do app tem
if (PREVIEW) document.body.classList.add('preview');
const AUDIO_MIN_LEAD = 0.08;   // s de folga no áudio para absorver variação da rede
const AUDIO_MAX_LEAD = 0.35;   // acima disso descarta para não acumular atraso

const $ = (id) => document.getElementById(id);
const canvas = $('screen');
const ctx2d = canvas.getContext('2d');

let ws = null;
let videoDecoder = null;
let audioDecoder = null;
let waitingForKey = true;
let live = false;

let audioCtx = null;
let gainNode = null;
let audioPlayhead = 0;

function showOverlay(title, text = '') {
  $('overlayTitle').textContent = title;
  $('overlayText').textContent = text;
  $('overlay').hidden = false;
}

function setLive(value) {
  live = value;
  $('badge').classList.toggle('live', value);
  $('badgeText').textContent = value ? 'AO VIVO' : 'OFFLINE';
  if (value) $('overlay').hidden = true;
}

// ---------- Conexão ----------

function connect() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const auth = PREVIEW ? `role=preview&t=${encodeURIComponent(HOST_TOKEN)}` : `role=viewer&k=${encodeURIComponent(VIEW_KEY)}`;
  ws = new WebSocket(`${proto}://${location.host}/ws?${auth}`);
  ws.binaryType = 'arraybuffer';
  ws.onmessage = (e) => {
    if (typeof e.data === 'string') onControl(JSON.parse(e.data));
    else onPacket(e.data);
  };
  ws.onclose = () => {
    resetDecoders();
    setLive(false);
    if (closedReason === 'expired') return;   // link trocado: não adianta tentar de novo
    if (closedReason === 'full') {
      closedReason = null;
      setTimeout(connect, 10_000);
      return;
    }
    showOverlay('Sem conexão com o servidor', 'Tentando reconectar…');
    setTimeout(connect, 2000);
  };
}

let closedReason = null;

function onControl(msg) {
  switch (msg.type) {
    case 'hello':
      if (!msg.live) showOverlay('Aguardando a transmissão começar…', 'A imagem aparece aqui automaticamente.');
      else showOverlay('Conectando à transmissão…');
      break;
    case 'config':
      configureDecoders(msg);
      break;
    case 'offline':
      resetDecoders();
      setLive(false);
      showOverlay('Transmissão encerrada', 'Fique na página: se recomeçar, a imagem volta sozinha.');
      break;
    case 'full':
      closedReason = 'full';
      showOverlay('A transmissão está cheia', 'O limite de espectadores foi atingido. Tentando entrar de novo a cada 10 segundos…');
      break;
    case 'expired':
      closedReason = 'expired';
      showOverlay('Este link não vale mais', 'Quem está transmitindo gerou um link novo. Peça o endereço atualizado.');
      break;
  }
}

function onPacket(buf) {
  const view = new DataView(buf);
  const kind = view.getUint8(0);
  const key = (view.getUint8(1) & 1) === 1;
  const timestamp = view.getFloat64(2, true);
  const data = new Uint8Array(buf, 10);

  if (kind === 1) {
    if (!videoDecoder || videoDecoder.state !== 'configured') return;
    if (waitingForKey && !key) return;
    waitingForKey = false;
    // Se o computador do espectador não der conta, pula até o próximo quadro-chave.
    if (videoDecoder.decodeQueueSize > 30) { waitingForKey = true; return; }
    videoDecoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp, data }));
  } else if (kind === 2) {
    if (!audioDecoder || audioDecoder.state !== 'configured') return;
    audioDecoder.decode(new EncodedAudioChunk({ type: 'key', timestamp, data }));
  }
}

// ---------- Decodificação ----------

let framesDrawn = 0;
setInterval(() => {
  $('info').textContent = live ? `${canvas.width}×${canvas.height} · ${Math.round(framesDrawn / 2)} fps` : '';
  framesDrawn = 0;
}, 2000);

let configSeq = 0;

async function configureDecoders(cfg) {
  resetDecoders();
  const seq = configSeq;

  // Nem todo navegador decodifica H.264 (ex.: Chromium sem codecs proprietários em algumas distros Linux).
  const videoCfg = { codec: cfg.video.codec, optimizeForLatency: true };
  const { supported } = await VideoDecoder.isConfigSupported(videoCfg).catch(() => ({ supported: false }));
  if (seq !== configSeq) return;   // chegou outra config enquanto verificava
  if (!supported) {
    showOverlay('Seu navegador não reproduz vídeo H.264', 'Abra o link no Google Chrome, Microsoft Edge ou Safari.');
    return;
  }

  videoDecoder = new VideoDecoder({
    output: enqueueFrame,
    error: (e) => {
      // Decoder com erro fica fechado: recria e espera o próximo quadro-chave.
      console.error('VideoDecoder', e);
      if (seq === configSeq) setTimeout(() => seq === configSeq && configureDecoders(cfg), 500);
    },
  });
  videoDecoder.configure(videoCfg);

  if (cfg.audio && !PREVIEW && 'AudioDecoder' in window) {
    audioDecoder = new AudioDecoder({
      output: playAudio,
      error: (e) => console.error('AudioDecoder', e),
    });
    audioDecoder.configure(cfg.audio);
  }
}

// ---------- Exibição com ritmo constante ----------
//
// Os quadros chegam pela internet em intervalos irregulares. Em vez de desenhar cada um
// assim que chega, guardamos alguns em fila e mostramos cada quadro no horário dele
// (timestamp do streamer + atraso fixo). Assim 60 fps aparecem como 60 fps uniformes.

const PLAYOUT_DELAY_US = 80_000;     // folga para absorver a variação da rede
const MAX_AHEAD_US = 300_000;        // fila maior que isso = atraso acumulado: ressincroniza
const MAX_QUEUE = 30;

let frameQueue = [];
let playoutOffset = null;            // relógio local (µs) - timestamp do streamer
let lastQueuedTs = -Infinity;

function localMicros() {
  return performance.now() * 1000;
}

function enqueueFrame(frame) {
  // Timestamp voltou ou saltou (transmissão reiniciada): recomeça o relógio.
  if (frame.timestamp < lastQueuedTs || frame.timestamp - lastQueuedTs > 1_000_000) resyncPlayout(frame.timestamp);
  lastQueuedTs = frame.timestamp;

  frameQueue.push(frame);
  while (frameQueue.length > MAX_QUEUE) frameQueue.shift().close();

  // Chegando atrasado demais (rede engasgou) ou adiantado demais (fila crescendo): realinha.
  const due = frame.timestamp + playoutOffset;
  const now = localMicros();
  if (due < now - PLAYOUT_DELAY_US || due > now + MAX_AHEAD_US) resyncPlayout(frame.timestamp);
}

function resyncPlayout(ts) {
  playoutOffset = localMicros() + PLAYOUT_DELAY_US - ts;
}

function renderLoop() {
  requestAnimationFrame(renderLoop);
  if (!frameQueue.length || playoutOffset === null) return;

  // Mostra o quadro mais recente cujo horário já chegou; descarta os que ficaram para trás.
  const streamNow = localMicros() - playoutOffset;
  let show = null;
  while (frameQueue.length && frameQueue[0].timestamp <= streamNow) {
    show?.close();
    show = frameQueue.shift();
  }
  if (!show) return;

  if (canvas.width !== show.displayWidth || canvas.height !== show.displayHeight) {
    canvas.width = show.displayWidth;
    canvas.height = show.displayHeight;
  }
  ctx2d.drawImage(show, 0, 0);
  show.close();
  framesDrawn++;
  if (!live) setLive(true);
}
requestAnimationFrame(renderLoop);

function clearFrameQueue() {
  for (const f of frameQueue) f.close();
  frameQueue = [];
  playoutOffset = null;
  lastQueuedTs = -Infinity;
}

function resetDecoders() {
  clearFrameQueue();
  configSeq++;   // invalida verificações/tentativas assíncronas ainda pendentes
  for (const d of [videoDecoder, audioDecoder]) {
    if (d && d.state !== 'closed') d.close();
  }
  videoDecoder = audioDecoder = null;
  waitingForKey = true;
}

// ---------- Áudio ----------

function playAudio(data) {
  try {
    if (!audioCtx || audioCtx.state !== 'running') return;
    const frames = data.numberOfFrames;
    const buffer = audioCtx.createBuffer(data.numberOfChannels, frames, data.sampleRate);
    const plane = new Float32Array(frames);
    for (let c = 0; c < data.numberOfChannels; c++) {
      data.copyTo(plane, { planeIndex: c, format: 'f32-planar' });
      buffer.copyToChannel(plane, c);
    }

    const now = audioCtx.currentTime;
    if (audioPlayhead < now + 0.01 || audioPlayhead > now + AUDIO_MAX_LEAD) audioPlayhead = now + AUDIO_MIN_LEAD;
    const src = audioCtx.createBufferSource();
    src.buffer = buffer;
    src.connect(gainNode);
    src.start(audioPlayhead);
    audioPlayhead += buffer.duration;
  } finally {
    data.close();
  }
}

let muted = true;

function enableAudio() {
  if (!audioCtx) {
    audioCtx = new AudioContext({ latencyHint: 'interactive' });
    gainNode = audioCtx.createGain();
    gainNode.connect(audioCtx.destination);
  }
  audioCtx.resume();
  setMuted(false);
}

function setMuted(value) {
  muted = value;
  if (gainNode) gainNode.gain.value = muted ? 0 : Number($('volume').value);
  $('unmute').hidden = !muted || PREVIEW;
  $('muteBtn').textContent = muted ? '🔇 Som desligado' : '🔊 Som ligado';
}

// ---------- Controles ----------

// O navegador só libera áudio depois de uma interação: qualquer clique na página já ativa o som.
document.addEventListener('pointerdown', (e) => {
  if (!PREVIEW && (!audioCtx || audioCtx.state !== 'running') && e.target.id !== 'muteBtn') enableAudio();
}, { capture: true });
$('unmute').onclick = enableAudio;
$('muteBtn').onclick = () => (muted || !audioCtx ? enableAudio() : setMuted(true));
$('volume').oninput = () => {
  if (!audioCtx) enableAudio();
  else setMuted(false);
};

function toggleFullscreen() {
  if (document.fullscreenElement) document.exitFullscreen();
  else $('stage').requestFullscreen().catch(() => {});
}
$('fullscreen').onclick = toggleFullscreen;
canvas.ondblclick = toggleFullscreen;

// ---------- Início ----------

if (!('VideoDecoder' in window)) {
  showOverlay('Navegador não suportado', 'Abra este link no Chrome, Edge ou Safari atualizado (pelo link https).');
} else if (!VIEW_KEY && !PREVIEW) {
  showOverlay('Link inválido', 'Peça o link completo para quem está transmitindo.');
} else {
  setMuted(true);   // o navegador só libera som depois de um clique
  connect();
}
