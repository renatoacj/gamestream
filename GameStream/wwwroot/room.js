'use strict';

// Página da sala: mostra as telas compartilhadas (a do app e as dos convidados) como
// miniaturas, reproduz ao vivo a que for expandida e permite compartilhar a própria tela.
// Vídeo e áudio chegam já codificados pelo WebSocket e são decodificados com WebCodecs.
// Tudo passa pelo link HTTPS, sem P2P.

const params = new URLSearchParams(location.search);
const VIEW_KEY = params.get('k') || '';
// Dentro da janela do app: participa como anfitrião (token) e não mostra o botão de compartilhar.
const EMBEDDED = params.get('embedded') === '1';
const HOST_TOKEN = params.get('t') || '';
// Dentro do app de um convidado que entrou nesta sala: a tela é enviada pelo app (melhor qualidade),
// e "dev" identifica quais telas da sala são deste PC.
const IN_APP = params.get('app') === '1';
const MY_DEVICE = params.get('dev') || '';
const NAME_FROM_APP = (params.get('name') || '').trim().slice(0, 24);
if (EMBEDDED || IN_APP) document.body.classList.add('embedded');

const HEADER = 12;            // [tipo][flags][transmissão u16][timestamp f64]
const KIND_VIDEO = 1, KIND_AUDIO = 2, KIND_THUMB = 3;
const HOST_STREAM = 0;        // a transmissão feita pelo app
const AUDIO_MIN_LEAD = 0.08;  // s de folga no áudio para absorver variação da rede
const AUDIO_MAX_LEAD = 0.35;  // acima disso descarta para não acumular atraso

const $ = (id) => document.getElementById(id);
const canvas = $('screen');
const ctx2d = canvas.getContext('2d');

let ws = null;
let myId = null;
let isHost = false;
let canShare = true;
let closedReason = null;

let people = [];
const streams = new Map();        // número → { no, owner, name, audio, thumbUrl }
const pendingThumbs = new Map();  // miniatura que chegou antes da lista da sala
let watching = null;              // número da transmissão expandida
let autoWatch = true;             // abre sozinho quando só há uma tela (até o usuário voltar à lista)

// ---------- Nome ----------

function storedName() {
  if (NAME_FROM_APP) return NAME_FROM_APP;   // no app, o nome é o configurado lá
  try { return localStorage.getItem('gamestream-name') || ''; } catch { return ''; }
}

function setName(name) {
  name = name.trim().slice(0, 24);
  if (!name) return;
  try { localStorage.setItem('gamestream-name', name); } catch { /* sem armazenamento */ }
  $('myName').textContent = name;
  send({ type: 'name', name });
}

function askName() {
  $('nameInput').value = storedName();
  $('nameDialog').showModal();
  $('nameInput').select();
}

$('nameBtn').onclick = askName;
$('nameBtn').hidden = NAME_FROM_APP !== '';   // no app, o nome é mudado nos controles do app
$('nameOk').onclick = () => {
  setName($('nameInput').value || 'Convidado');
  $('nameDialog').close();
};
$('nameInput').onkeydown = (e) => { if (e.key === 'Enter') $('nameOk').click(); };

// ---------- Conexão ----------

function connect() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const auth = EMBEDDED ? `role=host&t=${encodeURIComponent(HOST_TOKEN)}` : `role=viewer&k=${encodeURIComponent(VIEW_KEY)}`;
  const dev = MY_DEVICE ? `&dev=${encodeURIComponent(MY_DEVICE)}` : '';
  ws = new WebSocket(`${proto}://${location.host}/ws?${auth}${dev}`);
  ws.binaryType = 'arraybuffer';
  ws.onopen = () => send({ type: 'hello', name: storedName() || (EMBEDDED ? 'Anfitrião' : 'Convidado') });
  ws.onmessage = (e) => {
    if (typeof e.data === 'string') onControl(JSON.parse(e.data));
    else onPacket(e.data);
  };
  ws.onclose = () => {
    stopSharing(false);
    resetDecoders();
    watching = null;
    streams.clear();
    people = [];
    render();
    if (closedReason === 'expired') return;   // link trocado: não adianta tentar de novo
    if (closedReason === 'full') {
      closedReason = null;
      setTimeout(connect, 10_000);
      return;
    }
    if (everConnected) {
      failedAttempts = 0;
      everConnected = false;
      showRoomMessage('Sem conexão com a sala', 'Tentando reconectar…');
      setTimeout(connect, 2000);
      return;
    }
    // Nunca chegou a entrar: código/link errado ou sala fechada. Insistir rápido não ajuda
    // (e tentativas erradas demais bloqueiam a entrada por código), então as tentativas vão se espaçando.
    failedAttempts++;
    const wait = Math.min(60, 2 * 2 ** Math.min(failedAttempts, 5));
    showRoomMessage('Não foi possível entrar na sala',
      `Confira o código ou o link com quem criou a sala. Nova tentativa em ${wait} segundos…`);
    setTimeout(connect, wait * 1000);
  };
}

let everConnected = false;   // recebeu o "hello" desta conexão
let failedAttempts = 0;

function send(msg) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
}

function onControl(msg) {
  switch (msg.type) {
    case 'hello':
      myId = msg.id;
      isHost = msg.host;
      everConnected = true;
      roomMessage = null;
      break;

    case 'room':
      people = msg.people;
      canShare = msg.canShare;
      updateStreams(msg.streams);
      render();
      break;

    case 'config':
      if (msg.stream === watching) configureDecoders(msg);
      break;

    case 'published':
      publisher?.onPublished(msg.stream);
      break;

    case 'unpublished':
      stopSharing(false);
      toast(msg.reason);
      break;

    case 'keyframe':
      publisher?.forceKeyframe();
      break;

    case 'full':
      closedReason = 'full';
      showRoomMessage('A sala está cheia', 'O limite de pessoas foi atingido. Tentando entrar de novo a cada 10 segundos…');
      break;

    case 'expired':
      closedReason = 'expired';
      showRoomMessage('Este link não vale mais', 'Quem criou a sala gerou um link novo. Peça o endereço atualizado.');
      break;

    case 'removed':
      closedReason = 'expired';   // não tenta entrar de novo
      showRoomMessage('Você foi removido da sala', 'Quem criou a sala encerrou a sua participação.');
      break;
  }
}

function updateStreams(list) {
  const seen = new Set();
  for (const s of list) {
    seen.add(s.no);
    const current = streams.get(s.no) || { thumbUrl: pendingThumbs.get(s.no) || null };
    pendingThumbs.delete(s.no);
    streams.set(s.no, { ...current, ...s });
  }
  for (const [no, s] of streams) {
    if (seen.has(no)) continue;
    if (s.thumbUrl) URL.revokeObjectURL(s.thumbUrl);
    streams.delete(no);
  }

  if (watching !== null && !streams.has(watching)) {
    // A tela que eu assistia acabou: volta para a lista.
    resetDecoders();
    watching = null;
    toast('A transmissão que você assistia foi encerrada.');
  }

  // Só uma tela na sala (o caso mais comum): já abre direto nela.
  // A própria transmissão não abre sozinha: fica só como miniatura, para o PC de quem
  // transmite não gastar placa de vídeo e memória decodificando a própria tela.
  if (watching === null && autoWatch) {
    const others = [...streams.values()].filter((s) => !isMine(s) && !isOwnBroadcast(s));
    if (others.length === 1) watch(others[0].no, false);
  }
}

// A transmissão do app, vista de dentro do próprio app (quem criou a sala). Diferente de "isMine",
// dá para abrir a prévia ao vivo com um clique, se a pessoa quiser conferir.
const isOwnBroadcast = (s) => EMBEDDED && s.no === HOST_STREAM;

// Minha tela: enviada por esta página ou pelo app deste mesmo PC.
const isMine = (s) => s.owner !== '' && (s.owner === myId || (MY_DEVICE !== '' && s.dev === MY_DEVICE));

function onPacket(buf) {
  if (buf.byteLength <= HEADER) return;
  const view = new DataView(buf);
  const kind = view.getUint8(0);
  const key = (view.getUint8(1) & 1) === 1;
  const no = view.getUint16(2, true);

  if (kind === KIND_THUMB) {
    const url = URL.createObjectURL(new Blob([new Uint8Array(buf, HEADER)], { type: 'image/jpeg' }));
    const s = streams.get(no);
    if (s) {
      if (s.thumbUrl) URL.revokeObjectURL(s.thumbUrl);
      s.thumbUrl = url;
      const img = document.querySelector(`.tile[data-no="${no}"] img`);
      if (img) img.src = url; else renderTiles();
    } else {
      if (pendingThumbs.has(no)) URL.revokeObjectURL(pendingThumbs.get(no));
      pendingThumbs.set(no, url);
    }
    return;
  }

  if (no !== watching) return;
  const timestamp = view.getFloat64(4, true);
  const data = new Uint8Array(buf, HEADER);

  if (kind === KIND_VIDEO) {
    if (!videoDecoder || videoDecoder.state !== 'configured') return;
    if (waitingForKey && !key) return;
    waitingForKey = false;
    // Se o computador de quem assiste não der conta, pula até o próximo quadro-chave.
    if (videoDecoder.decodeQueueSize > 30) { waitingForKey = true; return; }
    videoDecoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp, data }));
  } else if (kind === KIND_AUDIO) {
    if (!audioDecoder || audioDecoder.state !== 'configured') return;
    audioDecoder.decode(new EncodedAudioChunk({ type: 'key', timestamp, data }));
  }
}

// ---------- Escolher o que assistir ----------

function watch(no, byUser = true) {
  if (no === watching) return;
  const s = streams.get(no);
  if (!s || isMine(s)) return;
  if (watching !== null) send({ type: 'unwatch', stream: watching });
  resetDecoders();
  watching = no;
  if (byUser) autoWatch = true;
  showOverlay('Conectando à transmissão…');
  send({ type: 'watch', stream: no });
  render();
}

function backToList() {
  if (watching !== null) send({ type: 'unwatch', stream: watching });
  resetDecoders();
  watching = null;
  autoWatch = false;
  if (document.fullscreenElement) document.exitFullscreen();
  render();
}

$('backBtn').onclick = backToList;

// ---------- Interface ----------

let roomMessage = { title: 'Conectando…', text: '' };

function showRoomMessage(title, text = '') {
  roomMessage = { title, text };
  render();
}

function showOverlay(title, text = '') {
  $('overlayTitle').textContent = title;
  $('overlayText').textContent = text;
  $('overlay').hidden = false;
}

let toastTimer = null;
function toast(text) {
  if (!text) return;
  $('toast').textContent = text;
  $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ($('toast').hidden = true), 6000);
}

function render() {
  // Quem está na sala
  const box = $('people');
  box.textContent = '';
  for (const p of people) {
    const chip = document.createElement('span');
    chip.className = 'chip' + (p.id === myId ? ' me' : '') + (p.host ? ' host' : '');
    chip.textContent = p.name + (p.id === myId ? ' (você)' : '');
    // Quem criou a sala pode tirar alguém dela.
    if (isHost && !p.host) {
      const remove = document.createElement('button');
      remove.className = 'chip-remove';
      remove.textContent = '✕';
      remove.title = `Remover ${p.name} da sala`;
      remove.onclick = () => {
        if (confirm(`Remover ${p.name} da sala? A pessoa é desconectada e não consegue voltar enquanto o app estiver aberto.`)) {
          send({ type: 'remove', id: p.id });
        }
      };
      chip.append(remove);
    }
    box.append(chip);
  }
  const me = people.find((p) => p.id === myId);
  if (me) $('myName').textContent = me.name;

  // Tela expandida
  const s = watching !== null ? streams.get(watching) : null;
  $('stage').hidden = !s;
  if (s) $('stageName').textContent = s.name;

  renderTiles();

  // Compartilhar: só em navegadores com captura + codificação (Chrome/Edge no computador)
  const sharing = !!publisher;
  $('shareBtn').hidden = EMBEDDED || IN_APP || !ws || ws.readyState !== WebSocket.OPEN || (!sharing && !canShare);
  $('shareQuality').hidden = $('shareBtn').hidden || sharing;
  $('shareBtn').disabled = !sharing && !SHARE_SUPPORTED;
  $('shareBtn').title = SHARE_SUPPORTED ? '' : 'Para compartilhar a tela, use o Chrome ou o Edge no computador.';
  $('shareBtn').textContent = sharing ? '■ Parar de compartilhar' : '🖥 Compartilhar minha tela';
  $('shareBtn').className = sharing ? 'danger' : 'primary';
}

function renderTiles() {
  const box = $('tiles');
  box.className = 'tiles ' + (watching !== null ? 'strip' : 'grid');
  box.textContent = '';

  if (roomMessage || !streams.size) {
    const m = roomMessage || {
      title: 'Ninguém está compartilhando a tela ainda',
      text: canShare && !EMBEDDED ? 'Quando alguém começar, a tela aparece aqui. Você também pode compartilhar a sua.'
        : 'Quando alguém começar, a tela aparece aqui.',
    };
    const empty = document.createElement('div');
    empty.className = 'empty-room';
    const big = document.createElement('div');
    big.className = 'big';
    big.textContent = m.title;
    empty.append(big, m.text);
    box.append(empty);
    return;
  }

  for (const s of [...streams.values()].sort((a, b) => a.no - b.no)) {
    const mine = isMine(s);
    const tile = document.createElement('div');
    tile.className = 'tile' + (s.no === watching ? ' selected' : '') + (mine ? ' mine' : '');
    tile.dataset.no = s.no;
    tile.tabIndex = mine ? -1 : 0;
    const own = isOwnBroadcast(s);
    tile.title = mine ? 'Esta é a sua tela'
      : own ? 'Sua transmissão. A prévia fica pausada para poupar o PC; clique para ver ao vivo.'
      : `Assistir à tela de ${s.name}`;

    const thumb = document.createElement('div');
    thumb.className = 'thumb';
    if (s.thumbUrl) {
      const img = document.createElement('img');
      img.src = s.thumbUrl;
      img.alt = '';
      thumb.append(img);
    } else {
      thumb.textContent = '🖥';
    }
    const live = document.createElement('span');
    live.className = 'live-tag';
    live.textContent = 'AO VIVO';
    const hint = document.createElement('span');
    hint.className = 'expand-hint';
    hint.textContent = own ? '⤢ Ver prévia ao vivo' : '⤢ Assistir';
    thumb.append(live, hint);

    const meta = document.createElement('div');
    meta.className = 'meta';
    const name = document.createElement('b');
    name.textContent = s.name;
    const tag = document.createElement('small');
    tag.textContent = mine ? 'você' : own ? (s.no === watching ? 'você' : 'você · prévia pausada') : s.audio ? '🔊' : '';
    meta.append(name, tag);

    if (isHost && s.owner !== '') {
      const kick = document.createElement('button');
      kick.className = 'kick';
      kick.textContent = '✕';
      kick.title = `Encerrar o compartilhamento de ${s.name}`;
      kick.onclick = (e) => {
        e.stopPropagation();
        if (confirm(`Encerrar o compartilhamento de ${s.name}?`)) send({ type: 'kick', stream: s.no });
      };
      meta.append(kick);
    }

    tile.append(thumb, meta);
    if (!mine) {
      tile.onclick = () => watch(s.no);
      tile.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') watch(s.no); };
    }
    box.append(tile);
  }
}

// ---------- Decodificação ----------

let videoDecoder = null;
let audioDecoder = null;
let waitingForKey = true;
let live = false;
let configSeq = 0;

let framesDrawn = 0;
setInterval(() => {
  $('info').textContent = live && watching !== null ? `${canvas.width}×${canvas.height} · ${Math.round(framesDrawn / 2)} fps` : '';
  framesDrawn = 0;
}, 2000);

async function configureDecoders(cfg) {
  resetDecoders();
  const seq = configSeq;

  // Nem todo navegador decodifica H.264 (ex.: Chromium sem codecs proprietários em algumas distros Linux).
  const videoCfg = { codec: cfg.video.codec, optimizeForLatency: true };
  const { supported } = await VideoDecoder.isConfigSupported(videoCfg).catch(() => ({ supported: false }));
  if (seq !== configSeq) return;   // chegou outra config enquanto verificava
  if (!supported) {
    showOverlay('Seu navegador não reproduz este vídeo', 'Abra o link no Google Chrome, Microsoft Edge ou Safari.');
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

  // Dentro do app, a transmissão do próprio app fica sem som (senão você ouviria o jogo duas vezes).
  const ownStream = EMBEDDED && cfg.stream === HOST_STREAM;
  if (cfg.audio && !ownStream && 'AudioDecoder' in window) {
    audioDecoder = new AudioDecoder({
      output: playAudio,
      error: (e) => console.error('AudioDecoder', e),
    });
    audioDecoder.configure(cfg.audio);
  }
  updateUnmuteHint();
}

function resetDecoders() {
  clearFrameQueue();
  configSeq++;   // invalida verificações/tentativas assíncronas ainda pendentes
  for (const d of [videoDecoder, audioDecoder]) {
    if (d && d.state !== 'closed') d.close();
  }
  videoDecoder = audioDecoder = null;
  waitingForKey = true;
  live = false;
  updateUnmuteHint();
}

// ---------- Exibição com ritmo constante ----------
//
// Os quadros chegam pela internet em intervalos irregulares. Em vez de desenhar cada um
// assim que chega, guardamos alguns em fila e mostramos cada quadro no horário dele
// (timestamp de quem transmite + atraso fixo). Assim 60 fps aparecem como 60 fps uniformes.

const PLAYOUT_DELAY_US = 80_000;     // folga para absorver a variação da rede
const MAX_AHEAD_US = 300_000;        // fila maior que isso = atraso acumulado: ressincroniza
const MAX_QUEUE = 30;

let frameQueue = [];
let playoutOffset = null;            // relógio local (µs) - timestamp de quem transmite
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
  if (!live) {
    live = true;
    $('overlay').hidden = true;
  }
}
requestAnimationFrame(renderLoop);

function clearFrameQueue() {
  ctx2d.clearRect(0, 0, canvas.width, canvas.height);   // não deixa a imagem da tela anterior
  for (const f of frameQueue) f.close();
  frameQueue = [];
  playoutOffset = null;
  lastQueuedTs = -Infinity;
}

// ---------- Áudio ----------

let audioCtx = null;
let gainNode = null;
let audioPlayhead = 0;
let muted = true;

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

function enableAudio() {
  if (!audioCtx) {
    audioCtx = new AudioContext({ latencyHint: 'interactive' });
    gainNode = audioCtx.createGain();
    gainNode.connect(audioCtx.destination);
  }
  audioCtx.resume();
  setMuted(false);
}

// O aviso "clique para ativar o som" só aparece quando a tela aberta tem áudio para tocar.
function updateUnmuteHint() {
  $('unmute').hidden = !muted || !audioDecoder;
}

function setMuted(value) {
  muted = value;
  if (gainNode) gainNode.gain.value = muted ? 0 : Number($('volume').value);
  updateUnmuteHint();
  $('muteBtn').textContent = muted ? '🔇 Som desligado' : '🔊 Som ligado';
}

// O navegador só libera áudio depois de uma interação: qualquer clique na página já ativa o som.
document.addEventListener('pointerdown', (e) => {
  if ((!audioCtx || audioCtx.state !== 'running') && e.target.id !== 'muteBtn') enableAudio();
}, { capture: true });
$('unmute').onclick = enableAudio;
$('muteBtn').onclick = () => (muted || !audioCtx ? enableAudio() : setMuted(true));
$('volume').oninput = () => {
  if (!audioCtx) enableAudio();
  else setMuted(false);
};

function toggleFullscreen() {
  if (document.fullscreenElement) document.exitFullscreen();
  else if (watching !== null) $('stage').requestFullscreen().catch(() => {});
}
$('fullscreen').onclick = toggleFullscreen;
canvas.ondblclick = toggleFullscreen;

// ---------- Compartilhar a minha tela ----------
//
// A captura (getDisplayMedia) fica nesta página; a codificação roda num Worker
// (publisher-worker.js), que continua trabalhando com a aba em segundo plano —
// o normal para quem está jogando enquanto compartilha.

const SHARE_SUPPORTED = !!navigator.mediaDevices?.getDisplayMedia &&
  'MediaStreamTrackProcessor' in window && 'VideoEncoder' in window && 'Worker' in window;

let publisher = null;

async function pickVideoCodec(width, height, fps, bitrate) {
  // H.264 primeiro (hardware na maioria dos PCs); VP8 como reserva universal.
  for (const codec of ['avc1.64002a', 'avc1.4d002a', 'avc1.42e02a', 'vp8']) {
    for (const hardwareAcceleration of ['prefer-hardware', 'no-preference']) {
      const cfg = { codec, width, height, bitrate, framerate: fps, latencyMode: 'realtime', hardwareAcceleration };
      if (codec.startsWith('avc1')) cfg.avc = { format: 'annexb' };
      const { supported } = await VideoEncoder.isConfigSupported(cfg).catch(() => ({ supported: false }));
      if (supported) return cfg;
    }
  }
  return null;
}

async function startSharing() {
  const [size, fps, kbps] = $('shareQuality').value.split('@');
  const [width, height] = size.split('x').map(Number);
  let media;
  try {
    media = await navigator.mediaDevices.getDisplayMedia({
      video: { width: { max: width }, height: { max: height }, frameRate: { ideal: Number(fps), max: Number(fps) } },
      audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
      systemAudio: 'include',
      surfaceSwitching: 'include',
    });
  } catch (e) {
    if (e.name !== 'NotAllowedError') toast('Não foi possível capturar a tela: ' + e.message);
    return;
  }
  await startPublishing(media, { width, height, fps: Number(fps), bitrate: Number(kbps) * 1000 });
}

/** Publica um MediaStream na sala. Separado de startSharing para poder ser testado com um vídeo sintético. */
async function startPublishing(media, q) {
  stopSharing(true);
  const videoTrack = media.getVideoTracks()[0];
  const audioTrack = media.getAudioTracks()[0] || null;
  videoTrack.contentHint = 'motion';

  const encoderConfig = await pickVideoCodec(q.width, q.height, q.fps, q.bitrate);
  if (!encoderConfig) {
    for (const t of media.getTracks()) t.stop();
    toast('Este navegador não tem um codificador de vídeo compatível.');
    return;
  }

  const worker = new Worker('publisher-worker.js');
  const p = publisher = {
    media, worker, streamNo: null, congested: false,
    onPublished(no) {
      this.streamNo = no;
      worker.postMessage({ type: 'keyframe', thumbnail: true });
      render();
    },
    forceKeyframe() { worker.postMessage({ type: 'keyframe' }); },
  };

  worker.onmessage = (e) => {
    const m = e.data;
    if (m.type === 'packet') {
      if (p.streamNo === null || !ws || ws.readyState !== WebSocket.OPEN) return;
      const isVideo = new Uint8Array(m.buf, 0, 1)[0] === KIND_VIDEO;
      // Minha internet não está dando conta: pula vídeo até esvaziar e recomeça num quadro-chave.
      if (isVideo && ws.bufferedAmount > 3 * 1024 * 1024) { p.congested = true; return; }
      if (isVideo && p.congested) {
        const key = (new Uint8Array(m.buf, 1, 1)[0] & 1) === 1;
        if (!key) { worker.postMessage({ type: 'keyframe' }); return; }
        p.congested = false;
      }
      ws.send(m.buf);
    } else if (m.type === 'error') {
      stopSharing(true);
      toast('O compartilhamento parou: ' + m.message);
    }
  };

  const videoReadable = new MediaStreamTrackProcessor({ track: videoTrack }).readable;
  const audioReadable = audioTrack ? new MediaStreamTrackProcessor({ track: audioTrack }).readable : null;
  worker.postMessage(
    { type: 'start', video: videoReadable, audio: audioReadable, encoderConfig },
    audioReadable ? [videoReadable, audioReadable] : [videoReadable],
  );

  // O usuário clicou em "Parar de compartilhar" na barra do navegador.
  videoTrack.addEventListener('ended', () => { if (publisher === p) stopSharing(true); });

  send({
    type: 'publish',
    video: { codec: encoderConfig.codec },
    audio: audioTrack ? { codec: 'opus', sampleRate: 48000, numberOfChannels: 2 } : null,
  });
  render();
}

function stopSharing(notifyServer) {
  const p = publisher;
  if (!p) return;
  publisher = null;
  p.worker.postMessage({ type: 'stop' });
  setTimeout(() => p.worker.terminate(), 500);
  for (const t of p.media.getTracks()) t.stop();
  if (notifyServer) send({ type: 'unpublish' });
  render();
}

$('shareBtn').onclick = () => (publisher ? stopSharing(true) : startSharing());

// ---------- Início ----------

if (!('VideoDecoder' in window)) {
  showRoomMessage('Navegador não suportado', 'Abra este link no Chrome, Edge ou Safari atualizado (pelo link https).');
} else if (!VIEW_KEY && !EMBEDDED) {
  showRoomMessage('Link inválido', 'Peça o link completo para quem criou a sala.');
} else {
  setMuted(true);
  $('myName').textContent = storedName() || (EMBEDDED ? 'Anfitrião' : 'Convidado');
  render();
  connect();
  if (!storedName() && !EMBEDDED) askName();
}
