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
// Versão do app (só dentro do app): vai junto ao entrar, para todos verem quem está numa versão diferente.
const APP_VERSION = /^\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(params.get('ver') || '') ? params.get('ver') : '';
// Sala de um amigo aberta no app: a página é a deste app (mesma versão, som tocado pelo app) e só os dados
// vêm do servidor do amigo, pelo túnel. Só endereços de túnel da Cloudflare são aceitos.
const REMOTE = (() => {
  const r = params.get('remote') || '';
  return IN_APP && /^https:\/\/[a-z0-9-]+\.trycloudflare\.com$/.test(r) ? r : '';
})();
if (EMBEDDED || IN_APP) document.body.classList.add('embedded');
// Dentro do app, o som é tocado pelo próprio GameStream.exe (ver RoomAudioPlayer.cs): o WebView2 toca áudio
// num processo dele, que programas de mixagem por aplicativo (MixLine, Voicemeeter) não reconhecem como o app.
// A página só avisa qual tela toca som, com que volume.
const NATIVE_AUDIO = (EMBEDDED || IN_APP) && window.parent !== window;

const HEADER = 12;            // [tipo][flags][transmissão u16][timestamp f64]
const KIND_VIDEO = 1, KIND_AUDIO = 2, KIND_THUMB = 3;
const HOST_STREAM = 0;        // a transmissão feita pelo app
const AUDIO_MIN_LEAD = 0.08;  // s de folga no áudio para absorver variação da rede
const AUDIO_MAX_LEAD = 0.45;  // acima disso descarta para não acumular atraso

const $ = (id) => document.getElementById(id);

let ws = null;
let myId = null;
let isHost = false;
let canShare = true;
let roomName = '';
let closedReason = null;

/** Identificador desta aba (o mesmo ao recarregar a página). */
const CLIENT_ID = (() => {
  const fresh = () => [...crypto.getRandomValues(new Uint8Array(8))].map((b) => b.toString(16).padStart(2, '0')).join('');
  try {
    let id = sessionStorage.getItem('gamestream-client');
    if (!/^[0-9a-f]{16}$/.test(id || '')) { id = fresh(); sessionStorage.setItem('gamestream-client', id); }
    return id;
  } catch {
    return fresh();
  }
})();

let people = [];
const streams = new Map();        // número → { no, owner, name, audio, thumbUrl }
const pendingThumbs = new Map();  // miniatura que chegou antes da lista da sala
let watched = [];                 // telas abertas (até duas, lado a lado)
let audioNo = null;               // das telas abertas, a que toca som (uma por vez)
const MAX_WATCHED = 2;
const players = new Map();        // número da tela → Player (quadro + decodificadores)
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
  const proto = REMOTE || location.protocol === 'https:' ? 'wss' : 'ws';
  const server = REMOTE ? REMOTE.slice('https://'.length) : location.host;
  const auth = EMBEDDED ? `role=host&t=${encodeURIComponent(HOST_TOKEN)}` : `role=viewer&k=${encodeURIComponent(VIEW_KEY)}`;
  // Cada página tem um identificador: se ela reconectar, o servidor derruba a conexão antiga (senão a pessoa
  // aparece duas vezes enquanto a conexão velha não cai). No app, é o identificador do PC.
  const id = MY_DEVICE || (EMBEDDED ? '' : CLIENT_ID);
  const dev = id ? `&dev=${encodeURIComponent(id)}` : '';
  ws = new WebSocket(`${proto}://${server}/ws?${auth}${dev}`);
  ws.binaryType = 'arraybuffer';
  ws.onopen = () => send({
    type: 'hello',
    name: storedName() || (EMBEDDED ? 'Anfitrião' : 'Convidado'),
    ...(APP_VERSION ? { version: APP_VERSION } : {}),
  });
  ws.onmessage = (e) => {
    if (typeof e.data === 'string') onControl(JSON.parse(e.data));
    else onPacket(e.data);
  };
  ws.onclose = () => {
    stopSharing(false);
    for (const no of [...watched]) closePlayer(no, false);
    streams.clear();
    people = [];
    render();
    if (closedReason === 'expired') return;   // link trocado: não adianta tentar de novo
    if (closedReason === 'closed') {
      // Sala fechada por enquanto: confere de tempos em tempos e entra sozinho quando ela reabrir.
      closedReason = null;
      setTimeout(connect, 5000);
      return;
    }
    if (closedReason === 'full') {
      closedReason = null;
      setTimeout(connect, 10_000);
      return;
    }
    if (everConnected || lostAttempts > 0) {
      // Já esteve na sala: insiste. Se não volta logo, provavelmente quem criou fechou o app (o link muda).
      failedAttempts = 0;
      everConnected = false;
      lostAttempts++;
      if (lostAttempts > 4) {
        showRoomMessage('A sala não está respondendo',
          'Se quem a criou fechou o GameStream ou a internet dele caiu, o link mudou: peça o novo. Continuamos tentando…');
      } else {
        showRoomMessage('Sem conexão com a sala', 'Tentando reconectar…');
      }
      setTimeout(connect, lostAttempts > 4 ? 10_000 : 2000);
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
let lostAttempts = 0;        // tentativas seguidas de voltar depois de ter estado na sala

function send(msg) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
}

function onControl(msg) {
  switch (msg.type) {
    case 'hello':
      myId = msg.id;
      isHost = msg.host;
      everConnected = true;
      lostAttempts = 0;
      roomMessage = null;
      break;

    case 'room':
      people = msg.people;
      canShare = msg.canShare;
      roomName = (msg.title || '').slice(0, 32);
      updateStreams(msg.streams);
      render();
      break;

    case 'config':
      players.get(msg.stream)?.configure(msg);
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

    case 'closed':
      closedReason = 'closed';
      showRoomMessage('A sala está fechada', 'Quando quem a criou abrir de novo, você entra sozinho.');
      break;

    case 'expired':
      closedReason = 'expired';
      showRoomMessage('Este link não vale mais', 'Quem criou a sala gerou um link novo. Peça o endereço atualizado.');
      break;

    case 'ping':
      send({ type: 'pong' });
      break;

    case 'replaced':
      // A mesma página conectou de novo (ex.: aba duplicada): esta deixa de valer.
      closedReason = 'expired';
      showRoomMessage('A sala foi aberta em outra aba', 'Use a outra aba ou recarregue esta página.');
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

  for (const no of [...watched]) {
    if (streams.has(no)) continue;
    closePlayer(no, false);
    toast('Uma transmissão que você assistia foi encerrada.');
  }

  // Só uma tela na sala (o caso mais comum): já abre direto nela.
  if (!watched.length && autoWatch) {
    const others = [...streams.values()].filter((s) => !isMine(s) && !isOwnBroadcast(s));
    if (others.length === 1) watch(others[0].no, false);
  }
}

// A transmissão do app, vista de dentro do próprio app (quem criou a sala). Como "isMine", não aparece
// na sala: quem transmite não precisa ver a própria tela (e o PC não gasta decodificando). Fica só o aviso.
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

  players.get(no)?.onPacket(kind, key, view.getFloat64(4, true), new Uint8Array(buf, HEADER));
}

// ---------- Escolher o que assistir ----------

/** Abre só esta tela (fecha as outras que estiverem abertas). */
function watch(no, byUser = true) {
  if (watched.length === 1 && watched[0] === no) return;
  const s = streams.get(no);
  if (!s || isMine(s) || isOwnBroadcast(s)) return;
  for (const other of [...watched]) if (other !== no) closePlayer(other, false);
  if (byUser) autoWatch = true;
  if (!players.has(no)) openPlayer(no);
  setAudioStream(no);
  render();
}

/** Abre esta tela ao lado da que já está aberta (no máximo duas; a segunda é trocada). */
function watchAlso(no) {
  const s = streams.get(no);
  if (!s || isMine(s) || isOwnBroadcast(s) || players.has(no)) return;
  if (watched.length >= MAX_WATCHED) closePlayer(watched[watched.length - 1], false);
  openPlayer(no);
  if (audioNo === null) setAudioStream(no);
  render();
}

function openPlayer(no) {
  const p = new Player(no);
  players.set(no, p);
  watched.push(no);
  $('panes').append(p.el);
  send({ type: 'watch', stream: no });
}

function closePlayer(no, rerender = true) {
  const p = players.get(no);
  if (!p) return;
  send({ type: 'unwatch', stream: no });
  p.destroy();
  players.delete(no);
  watched = watched.filter((x) => x !== no);
  if (audioNo === no) setAudioStream(watched[0] ?? null);
  if (rerender) render();
}

/** Escolhe qual das telas abertas toca som. */
function setAudioStream(no) {
  audioNo = no;
  for (const p of players.values()) p.setAudio(p.no === no);
  loadVolumeForWatched();   // volume lembrado para esta pessoa
  setMuted(muted);          // atualiza o botão de som com o nome
}

/** No app: avisa o GameStream qual tela toca som (ele mesmo toca). */
let lastAudioReport = '';
function reportAudio() {
  if (!NATIVE_AUDIO) return;
  const p = audioNo !== null ? players.get(audioNo) : null;
  const msg = { gs: 'audio', stream: p?.cfg?.audio ? audioNo : null, volume: Number($('volume').value), muted };
  const key = JSON.stringify(msg);
  if (key === lastAudioReport) return;
  lastAudioReport = key;
  window.parent.postMessage(msg, '*');
}

function backToList() {
  for (const no of [...watched]) closePlayer(no, false);
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

let toastTimer = null;
function toast(text) {
  if (!text) return;
  $('toast').textContent = text;
  $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ($('toast').hidden = true), 6000);
}

function render() {
  // "Transmissão" (só quem criou transmite) ou "Sala" (todos podem compartilhar)
  setLabel($('roomTitle'), canShare ? 'users' : 'radio', canShare ? roomName || 'Sala' : 'Transmissão');

  // Quem está na sala
  const box = $('people');
  box.textContent = '';
  for (const p of people) {
    const chip = document.createElement('span');
    chip.className = 'chip' + (p.id === myId ? ' me' : '') + (p.host ? ' host' : '');
    if (p.host) chip.append(icon('crown'));
    chip.append(p.name + (p.id === myId ? ' (você)' : ''));
    if (p.host) chip.title = 'Criou a sala';
    // Versões diferentes do app costumam ser a causa de coisas que não funcionam entre duas pessoas.
    const hostVersion = people.find((x) => x.host)?.v || '';
    if (p.v && hostVersion && p.v !== hostVersion) {
      chip.classList.add('other-version');
      chip.title = `${p.name} está no GameStream ${p.v}; quem criou a sala, no ${hostVersion}. Atualizem para a mesma versão.`;
    } else if (p.v) {
      chip.title = (chip.title ? chip.title + ' · ' : '') + `GameStream ${p.v}`;
    }
    // Quem criou a sala pode tirar alguém dela.
    if (isHost && !p.host) {
      const remove = document.createElement('button');
      remove.className = 'chip-remove';
      remove.append(icon('x'));
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

  // Telas abertas (uma ou duas lado a lado)
  $('stage').hidden = !watched.length;
  $('panes').classList.toggle('two', watched.length > 1);
  for (const p of players.values()) p.updateLabel();

  // A própria tela não aparece na sala; no lugar dela, só este aviso.
  const sharingMine = [...streams.values()].some((s) => isMine(s) || isOwnBroadcast(s)) || publisher?.streamNo != null;
  $('sharingBadge').hidden = !sharingMine;

  renderTiles();

  // Compartilhar: só em navegadores com captura + codificação (Chrome/Edge no computador)
  const sharing = !!publisher;
  $('shareBtn').hidden = EMBEDDED || IN_APP || !ws || ws.readyState !== WebSocket.OPEN || (!sharing && !canShare);
  $('shareQuality').hidden = $('shareBtn').hidden || sharing;
  $('shareBtn').disabled = !sharing && !SHARE_SUPPORTED;
  $('shareBtn').title = SHARE_SUPPORTED ? '' : 'Para compartilhar a tela, use o Chrome ou o Edge no computador.';
  setLabel($('shareBtn'), sharing ? 'stop' : 'monitor', sharing ? 'Parar de compartilhar' : 'Compartilhar minha tela');
  $('shareBtn').className = sharing ? 'danger' : 'primary';
}

function renderTiles() {
  const box = $('tiles');
  // A minha tela não entra na lista: basta o aviso "Sua tela está sendo transmitida" no topo.
  const list = [...streams.values()].filter((s) => !isMine(s) && !isOwnBroadcast(s)).sort((a, b) => a.no - b.no);
  const sharingMine = streams.size > list.length;
  box.className = 'tiles ' + (watched.length ? 'strip' : 'grid');
  box.textContent = '';

  if (roomMessage || !list.length) {
    const m = roomMessage || (sharingMine ? {
      title: 'Sua tela está sendo transmitida',
      text: canShare ? 'Quando mais alguém compartilhar, a tela aparece aqui.' : 'Quem tem o código ou o link está vendo ao vivo.',
    } : canShare ? {
      title: 'Ninguém está compartilhando a tela ainda',
      text: EMBEDDED || IN_APP ? 'Quando alguém começar, a tela aparece aqui.'
        : 'Quando alguém começar, a tela aparece aqui. Você também pode compartilhar a sua.',
    } : EMBEDDED ? {
      title: 'Pronto para transmitir',
      text: 'Escolha a janela abaixo e clique em Iniciar transmissão.',
    } : {
      title: 'A transmissão ainda não começou',
      text: 'Quando começar, a imagem aparece aqui sozinha.',
    });
    const empty = document.createElement('div');
    empty.className = 'empty-room';
    const big = document.createElement('div');
    big.className = 'big';
    big.textContent = m.title;
    empty.append(big, m.text);
    box.append(empty);
    return;
  }

  for (const s of list) {
    const isOpen = players.has(s.no);
    const tile = document.createElement('div');
    tile.className = 'tile' + (isOpen ? ' selected' : '');
    tile.dataset.no = s.no;
    tile.tabIndex = 0;
    tile.title = `Assistir à tela de ${s.name}`;

    const thumb = document.createElement('div');
    thumb.className = 'thumb';
    if (s.thumbUrl) {
      const img = document.createElement('img');
      img.src = s.thumbUrl;
      img.alt = '';
      thumb.append(img);
    } else {
      thumb.append(icon('monitor'));
    }
    const live = document.createElement('span');
    live.className = 'live-tag';
    live.textContent = 'AO VIVO';
    const hint = document.createElement('span');
    hint.className = 'expand-hint';
    hint.textContent = 'Assistir';
    thumb.append(live, hint);

    const meta = document.createElement('div');
    meta.className = 'meta';
    const name = document.createElement('b');
    name.textContent = s.name;
    meta.append(name);

    // Assistir junto: abre esta tela ao lado da que já está aberta.
    if (watched.length && !isOpen) {
      const both = document.createElement('button');
      both.className = 'speaker';
      both.append(icon('columns'));
      both.title = watched.length >= MAX_WATCHED
        ? `Assistir ${s.name} no lugar da segunda tela`
        : `Assistir ${s.name} ao lado da tela aberta`;
      both.onclick = (e) => { e.stopPropagation(); watchAlso(s.no); };
      meta.append(both);
    }

    // Alto-falante: só uma tela toca som. O ícone mostra qual está tocando;
    // clicar ativa o som desta tela (silenciando as outras) ou silencia a que está tocando.
    if (s.audio) {
      const playing = s.no === audioNo && !muted && audioReady();
      const speaker = document.createElement('button');
      speaker.className = 'speaker' + (playing ? ' on' : '');
      speaker.append(icon(playing ? 'volume' : 'volume-off'));
      speaker.title = playing ? `Silenciar ${s.name}` : `Ouvir ${s.name} (as outras telas ficam sem som)`;
      speaker.onclick = (e) => {
        e.stopPropagation();
        if (playing) {
          setMuted(true);
        } else {
          if (players.has(s.no)) setAudioStream(s.no); else watch(s.no);
          enableAudio();
        }
      };
      meta.append(speaker);
    }

    if (isHost && s.owner !== '') {
      const kick = document.createElement('button');
      kick.className = 'kick';
      kick.append(icon('x'));
      kick.title = `Encerrar o compartilhamento de ${s.name}`;
      kick.onclick = (e) => {
        e.stopPropagation();
        if (confirm(`Encerrar o compartilhamento de ${s.name}?`)) send({ type: 'kick', stream: s.no });
      };
      meta.append(kick);
    }

    tile.append(thumb, meta);
    tile.onclick = () => watch(s.no);
    tile.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') watch(s.no); };
    box.append(tile);
  }
}

// ---------- Reprodução: uma por tela aberta ----------
//
// Cada tela aberta tem o seu quadro, os seus decodificadores e a sua fila com ritmo constante.
// Os quadros chegam pela internet em intervalos irregulares: em vez de desenhar cada um assim que
// chega, guardamos alguns em fila e mostramos cada quadro no horário dele (timestamp de quem
// transmite + uma folga). A folga cresce quando quadros chegam atrasados e volta aos poucos.
// Só a tela escolhida para o som decodifica áudio.

const PLAYOUT_MIN_US = 80_000;       // folga mínima para absorver a variação da rede
const PLAYOUT_MAX_US = 400_000;      // folga máxima (mais que isso deixa o vídeo atrasado demais)
const PLAYOUT_STEP_US = 20_000;
const MAX_AHEAD_US = 250_000;        // fila maior que folga + isso = atraso acumulado: ressincroniza
const MAX_QUEUE = 30;

const localMicros = () => performance.now() * 1000;

class Player {
  constructor(no) {
    this.no = no;
    this.el = document.createElement('div');
    this.el.className = 'pane';
    this.canvas = document.createElement('canvas');
    this.canvas.width = 1280;
    this.canvas.height = 720;
    this.canvas.ondblclick = toggleFullscreen;
    this.ctx = this.canvas.getContext('2d');
    this.overlay = document.createElement('div');
    this.overlay.className = 'overlay';
    const top = document.createElement('div');
    top.className = 'pane-top';
    this.label = document.createElement('span');
    this.label.className = 'label';
    this.soundBtn = document.createElement('button');
    this.soundBtn.className = 'pane-sound';
    this.soundBtn.onclick = () => { setAudioStream(no); enableAudio(); };
    this.closeBtn = document.createElement('button');
    this.closeBtn.append(icon('x'));
    this.closeBtn.title = 'Fechar esta tela';
    this.closeBtn.onclick = () => closePlayer(no);
    top.append(this.label, this.soundBtn, this.closeBtn);
    this.el.append(this.canvas, this.overlay, top);

    this.cfg = null;
    this.videoDecoder = null;
    this.audioDecoder = null;
    this.wantAudio = false;
    this.seq = 0;                 // invalida verificações/tentativas assíncronas pendentes
    this.waitingForKey = true;
    this.live = false;
    this.queue = [];
    this.offset = null;           // relógio local (µs) - timestamp de quem transmite
    this.delay = PLAYOUT_MIN_US;  // folga atual
    this.lastLateAt = 0;
    this.lastTs = -Infinity;
    this.framesDrawn = 0;
    this.showOverlay('Conectando à transmissão…');
  }

  showOverlay(title, text = '') {
    const big = document.createElement('div');
    big.className = 'big';
    big.textContent = title;
    const box = document.createElement('div');
    box.append(big, text);
    this.overlay.replaceChildren(box);
    this.overlay.hidden = false;
  }

  updateLabel() {
    const s = streams.get(this.no);
    const two = watched.length > 1;
    const on = this.no === audioNo && !muted;
    this.label.textContent = s?.name || '';
    this.closeBtn.hidden = !two;
    this.soundBtn.hidden = !two || !s?.audio;
    this.soundBtn.replaceChildren(icon(on ? 'volume' : 'volume-off'));
    this.soundBtn.classList.toggle('on', on);
    this.soundBtn.title = on ? `Tocando o som de ${s?.name}` : `Ouvir ${s?.name} (a outra tela fica sem som)`;
    this.el.classList.toggle('audio', two && on);
  }

  async configure(cfg) {
    this.resetDecoders();
    this.cfg = cfg;
    const seq = this.seq;

    // Nem todo navegador decodifica H.264 (ex.: Chromium sem codecs proprietários em algumas distros Linux).
    const videoCfg = { codec: cfg.video.codec, optimizeForLatency: true };
    const { supported } = await VideoDecoder.isConfigSupported(videoCfg).catch(() => ({ supported: false }));
    if (seq !== this.seq) return;   // chegou outra config enquanto verificava
    if (!supported) {
      this.showOverlay('Seu navegador não reproduz este vídeo', 'Abra o link no Google Chrome, Microsoft Edge ou Safari.');
      return;
    }
    this.videoDecoder = new VideoDecoder({
      output: (frame) => this.enqueue(frame),
      error: (e) => {
        // Decoder com erro fica fechado: recria e espera o próximo quadro-chave.
        console.error('VideoDecoder', e);
        if (seq === this.seq) setTimeout(() => seq === this.seq && this.configure(cfg), 500);
      },
    });
    this.videoDecoder.configure(videoCfg);
    this.setAudio(this.wantAudio);
    reportAudio();   // agora se sabe se esta tela tem som
  }

  /** Liga ou desliga o som desta tela. */
  setAudio(on) {
    this.wantAudio = on;
    const should = !NATIVE_AUDIO && on && !!this.cfg?.audio && 'AudioDecoder' in window;
    if (should && !this.audioDecoder) {
      this.audioDecoder = new AudioDecoder({ output: playAudio, error: (e) => console.error('AudioDecoder', e) });
      this.audioDecoder.configure(this.cfg.audio);
    } else if (!should && this.audioDecoder) {
      if (this.audioDecoder.state !== 'closed') this.audioDecoder.close();
      this.audioDecoder = null;
    }
    updateUnmuteHint();
  }

  onPacket(kind, key, timestamp, data) {
    if (kind === KIND_VIDEO) {
      const d = this.videoDecoder;
      if (!d || d.state !== 'configured') return;
      if (this.waitingForKey && !key) return;
      this.waitingForKey = false;
      // Se o computador de quem assiste não der conta, pula até o próximo quadro-chave.
      if (d.decodeQueueSize > 30) { this.waitingForKey = true; return; }
      d.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp, data }));
    } else if (kind === KIND_AUDIO) {
      const d = this.audioDecoder;
      if (!d || d.state !== 'configured') return;
      d.decode(new EncodedAudioChunk({ type: 'key', timestamp, data }));
    }
  }

  enqueue(frame) {
    // Timestamp voltou ou saltou (transmissão reiniciada): recomeça o relógio.
    if (frame.timestamp < this.lastTs || frame.timestamp - this.lastTs > 1_000_000) this.resync(frame.timestamp);
    this.lastTs = frame.timestamp;
    this.queue.push(frame);
    while (this.queue.length > MAX_QUEUE) this.queue.shift().close();

    const due = frame.timestamp + this.offset;
    const now = localMicros();
    if (due < now) {
      // Chegou depois do horário de aparecer: a rede oscila mais que a folga atual, e o vídeo engasga.
      // Aumenta a folga um pouco (o vídeo fica um tiquinho mais atrasado, mas fluido).
      this.lastLateAt = now;
      if (this.delay < PLAYOUT_MAX_US) {
        this.delay += PLAYOUT_STEP_US;
        this.offset += PLAYOUT_STEP_US;
      }
      if (due < now - this.delay) this.resync(frame.timestamp);   // engasgo grande: realinha de vez
    } else if (due > now + this.delay + MAX_AHEAD_US) {
      this.resync(frame.timestamp);   // fila crescendo: volta para perto do ao vivo
    } else if (this.delay > PLAYOUT_MIN_US && now - this.lastLateAt > 20_000_000) {
      // 20 s sem atraso: reduz a folga aos poucos para voltar a ficar perto do ao vivo.
      this.delay -= PLAYOUT_STEP_US / 2;
      this.offset -= PLAYOUT_STEP_US / 2;
      this.lastLateAt = now - 10_000_000;
    }
  }

  resync(ts) {
    this.offset = localMicros() + this.delay - ts;
  }

  /** Mostra o quadro mais recente cujo horário já chegou; descarta os que ficaram para trás. */
  draw() {
    if (!this.queue.length || this.offset === null) return;
    const streamNow = localMicros() - this.offset;
    let show = null;
    while (this.queue.length && this.queue[0].timestamp <= streamNow) {
      show?.close();
      show = this.queue.shift();
    }
    if (!show) return;
    const c = this.canvas;
    if (c.width !== show.displayWidth || c.height !== show.displayHeight) {
      c.width = show.displayWidth;
      c.height = show.displayHeight;
    }
    this.ctx.drawImage(show, 0, 0);
    show.close();
    this.framesDrawn++;
    if (!this.live) {
      this.live = true;
      this.overlay.hidden = true;
    }
  }

  clearQueue() {
    this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);   // não deixa a imagem anterior
    for (const f of this.queue) f.close();
    this.queue = [];
    this.offset = null;
    this.lastTs = -Infinity;
  }

  resetDecoders() {
    this.clearQueue();
    this.seq++;
    for (const d of [this.videoDecoder, this.audioDecoder]) {
      if (d && d.state !== 'closed') d.close();
    }
    this.videoDecoder = this.audioDecoder = null;
    this.waitingForKey = true;
    this.live = false;
    updateUnmuteHint();
  }

  destroy() {
    this.resetDecoders();
    this.cfg = null;
    this.el.remove();
    updateUnmuteHint();
  }
}

function renderLoop() {
  requestAnimationFrame(renderLoop);
  for (const p of players.values()) p.draw();
}
requestAnimationFrame(renderLoop);

setInterval(() => {
  const ps = [...players.values()];
  $('info').textContent = ps.filter((p) => p.live)
    .map((p) => `${p.canvas.width}×${p.canvas.height} · ${Math.round(p.framesDrawn / 2)} fps`).join('   ');
  $('info').title = ps.map((p) => `Folga contra oscilação da rede: ${Math.round(p.delay / 1000)} ms`).join('\n');
  for (const p of ps) p.framesDrawn = 0;
}, 2000);

// ---------- Áudio ----------

let audioCtx = null;
let gainNode = null;
let audioPlayhead = 0;
let audioLead = AUDIO_MIN_LEAD;   // folga atual entre "agora" e o som agendado (cresce se houver falhas)
let lastAudioGap = -Infinity;
let audioGapCount = 0;            // quantas vezes o som falhou (diagnóstico)
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
    if (audioPlayhead < now + 0.01) {
      // A fila esvaziou: os pacotes chegaram atrasados (rede oscilando ou página sem prioridade).
      // Se isso se repete, aumenta a folga para o som não ficar picotado; com o tempo ela volta ao normal.
      if (now - lastAudioGap < 5) audioLead = Math.min(AUDIO_MAX_LEAD - 0.05, audioLead + 0.04);
      if (lastAudioGap > -Infinity) audioGapCount++;
      lastAudioGap = now;
      audioPlayhead = now + audioLead;
    } else if (audioPlayhead > now + AUDIO_MAX_LEAD) {
      audioPlayhead = now + audioLead;   // acumulou atraso: volta para perto do ao vivo
    } else if (now - lastAudioGap > 30 && audioLead > AUDIO_MIN_LEAD) {
      audioLead = Math.max(AUDIO_MIN_LEAD, audioLead - 0.01);   // meio minuto sem falhas: reduz a folga aos poucos
      lastAudioGap = now;
    }
    const src = audioCtx.createBufferSource();
    src.buffer = buffer;
    src.connect(gainNode);
    src.start(audioPlayhead);
    audioPlayhead += buffer.duration;
  } finally {
    data.close();
  }
}

/** O som pode tocar? No app quem toca é o GameStream.exe; no navegador, depois do primeiro clique. */
const audioReady = () => NATIVE_AUDIO || audioCtx?.state === 'running';

function enableAudio() {
  if (NATIVE_AUDIO) {
    // No app não abre áudio no WebView2 (ficaria uma entrada muda a mais nos mixers).
    setMuted(false);
    return;
  }
  if (!audioCtx) {
    audioCtx = new AudioContext({ latencyHint: 'interactive' });
    gainNode = audioCtx.createGain();
    // Limitador depois do volume: dá para passar de 100% (jogos costumam chegar baixos) sem distorcer.
    const limiter = audioCtx.createDynamicsCompressor();
    limiter.threshold.value = -3;
    limiter.knee.value = 0;
    limiter.ratio.value = 20;
    limiter.attack.value = 0.003;
    limiter.release.value = 0.1;
    gainNode.connect(limiter);
    limiter.connect(audioCtx.destination);
  }
  audioCtx.resume().then(renderTiles);
  setMuted(false);
}

// ---------- Volume por pessoa ----------
// Cada pessoa transmite num volume diferente, então o volume escolhido é lembrado por nome.

let savedVolumes = {};
try { savedVolumes = JSON.parse(localStorage.getItem('gamestream-volumes') || '{}'); } catch { /* sem armazenamento */ }

/** Nome de quem está na tela que toca som. */
function watchedName() {
  return audioNo !== null ? streams.get(audioNo)?.name || '' : '';
}

function loadVolumeForWatched() {
  const v = savedVolumes[watchedName()];
  $('volume').value = typeof v === 'number' ? v : 1;
  applyVolume();
}

function applyVolume() {
  const v = Number($('volume').value);
  if (document.activeElement !== $('volumeLabel')) $('volumeLabel').value = Math.round(v * 100);
  if (gainNode) gainNode.gain.value = muted ? 0 : v;
  reportAudio();
}

// O aviso "clique para ativar o som" só aparece quando a tela aberta tem áudio para tocar.
function updateUnmuteHint() {
  $('unmute').hidden = NATIVE_AUDIO || !muted || ![...players.values()].some((p) => p.audioDecoder);
}

function setMuted(value) {
  muted = value;
  applyVolume();
  updateUnmuteHint();
  const name = watchedName();
  setLabel($('muteBtn'), muted ? 'volume-off' : 'volume', muted ? 'Som desligado' : name ? `Som de ${name}` : 'Som ligado');
  for (const p of players.values()) p.updateLabel();
  renderTiles();   // atualiza os alto-falantes das miniaturas
}

// O navegador só libera áudio depois de uma interação: qualquer clique na página já ativa o som.
document.addEventListener('pointerdown', (e) => {
  if (!audioReady() && e.target.id !== 'muteBtn') enableAudio();
}, { capture: true });
$('unmute').onclick = enableAudio;
$('muteBtn').onclick = () => (muted || !audioReady() ? enableAudio() : setMuted(true));
$('volume').oninput = volumeChanged;

// A porcentagem também pode ser digitada (0 a 400%); Enter ou sair do campo aplica.
function typedVolume() {
  const n = Number(String($('volumeLabel').value).replace(',', '.'));
  if (!Number.isFinite(n)) { applyVolume(); return; }
  $('volume').value = Math.min(400, Math.max(0, n)) / 100;
  volumeChanged();
  $('volumeLabel').value = Math.round(Number($('volume').value) * 100);
}
$('volumeLabel').onchange = typedVolume;
$('volumeLabel').onkeydown = (e) => { if (e.key === 'Enter') { typedVolume(); $('volumeLabel').blur(); } };

function volumeChanged() {
  const name = watchedName();
  if (name) {
    savedVolumes[name] = Number($('volume').value);
    try { localStorage.setItem('gamestream-volumes', JSON.stringify(savedVolumes)); } catch { /* sem armazenamento */ }
  }
  if (!audioReady()) enableAudio();
  else if (muted) setMuted(false);
  else applyVolume();
}

function toggleFullscreen() {
  if (document.fullscreenElement) document.exitFullscreen();
  else if (watched.length) $('stage').requestFullscreen().catch(() => {});
}
$('fullscreen').onclick = toggleFullscreen;

// Controles sobre o vídeo: aparecem quando o mouse mexe e somem depois de alguns segundos parado
// (o cursor também some). Enquanto o mouse está em cima de um controle, eles ficam.
let controlsTimer = null;
function showControls() {
  const stage = $('stage');
  stage.classList.add('controls');
  stage.classList.remove('idle');
  clearTimeout(controlsTimer);
  controlsTimer = setTimeout(() => {
    if (stage.querySelector('.stage-top:hover, .stage-bottom:hover, .pane-top:hover')) return showControls();
    stage.classList.remove('controls');
    stage.classList.add('idle');
  }, 2500);
}
$('stage').addEventListener('pointermove', showControls);
$('stage').addEventListener('pointerdown', showControls);
$('stage').addEventListener('pointerleave', () => {
  if (document.fullscreenElement) return;
  clearTimeout(controlsTimer);
  $('stage').classList.remove('controls', 'idle');
});

// Em tela cheia só a área do vídeo aparece: o som e o volume vão junto, para o canto da tela.
document.addEventListener('fullscreenchange', () => {
  const full = document.fullscreenElement === $('stage');
  if (full) $('stageBottom').append($('soundControls'));
  else document.querySelector('footer.bar').append($('soundControls'));
  showControls();
  setLabel($('fullscreen'), 'maximize', full ? 'Sair da tela cheia' : 'Tela cheia');
});

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
  // Dentro do app o som já começa ligado (o app permite áudio sem clique); no navegador é preciso um clique.
  if (EMBEDDED || IN_APP) {
    try { enableAudio(); } catch { /* fica no "clique para ativar o som" */ }
  }
  $('myName').textContent = storedName() || (EMBEDDED ? 'Anfitrião' : 'Convidado');
  render();
  connect();
  if (!storedName() && !EMBEDDED) askName();
}
