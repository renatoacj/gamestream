'use strict';

// Interface do app. A captura e a codificação rodam no C# (FFmpeg + WASAPI);
// esta página só envia comandos e mostra o status.

const HOST_TOKEN = new URLSearchParams(location.search).get('t');
const $ = (id) => document.getElementById(id);
const api = (path) => `/api/${path}?t=${HOST_TOKEN}`;

// O padrão é a predefinição "Recomendado": funciona bem na maioria dos PCs e conexões.
const DEFAULTS = {
  height: 720, fps: 30, constantFps: true, cursor: true,
  encoder: 'auto', rateControl: 'cbr', bitrateKbps: 4500, preset: 'balanced',
  keyframeSeconds: 1, profile: 'high', audioMode: 'window', audioKbps: 160, audioGainDb: 'auto',
};

const PRESETS = {
  recommended: { height: 720,  fps: 30, constantFps: true, encoder: 'auto', rateControl: 'cbr', bitrateKbps: 4500,  preset: 'balanced', keyframeSeconds: 1, profile: 'high' },
  quality:     { height: 1080, fps: 60, constantFps: true, encoder: 'auto', rateControl: 'cbr', bitrateKbps: 10000, preset: 'quality' },
  balanced:    { height: 900,  fps: 60, constantFps: true, encoder: 'auto', rateControl: 'cbr', bitrateKbps: 8000,  preset: 'balanced' },
  cpu:         { height: 720,  fps: 30, constantFps: true, encoder: 'x264', rateControl: 'cbr', bitrateKbps: 4500,  preset: 'speed' },
  light:       { height: 540,  fps: 30, constantFps: true, encoder: 'auto', rateControl: 'cbr', bitrateKbps: 2500,  preset: 'balanced' },
};

const settingEls = () => [...document.querySelectorAll('[data-setting]')];

const PREFS_VERSION = 3;

let shownUrl;
let roomSrc = null;     // endereço carregado no quadro da sala
let joined = null;      // dados da sala de um amigo em que entramos (vem do status)
let sources = [];

// ---------- Configurações ----------

function readSettings() {
  const s = {};
  for (const el of settingEls()) {
    if (el.type === 'checkbox') s[el.id] = el.checked;
    else if (el.type === 'number' || /^\d/.test(el.value)) s[el.id] = Number(el.value);
    else s[el.id] = el.value;
  }
  return s;
}

function applySettings(s) {
  for (const el of settingEls()) {
    if (s[el.id] === undefined) continue;
    if (el.type === 'checkbox') el.checked = !!s[el.id];
    else el.value = String(s[el.id]);
  }
}

function savePrefs() {
  try {
    const src = sources.find((x) => x.id === $('source').value);
    localStorage.setItem('gamestream-settings', JSON.stringify({
      ...readSettings(),
      v: PREFS_VERSION,
      profilePreset: $('profilePreset').value,
      // A janela muda de identificador a cada execução; guardamos nome e programa para reencontrá-la.
      sourceName: src?.name, sourceDetail: src?.detail,
    }));
  } catch { /* armazenamento indisponível */ }
}

function loadPrefs() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem('gamestream-settings') || '{}'); } catch { /* nada salvo */ }
  // O padrão de ganho passou a ser "Automático": configurações salvas por versões antigas adotam o novo padrão.
  if (saved.v !== PREFS_VERSION) delete saved.audioGainDb;
  applySettings({ ...DEFAULTS, ...saved });
  // Primeira vez: "Recomendado". Depois: o que estava escolhido ("" = personalizado).
  const preset = saved.profilePreset === 'stable30' ? 'recommended' : saved.profilePreset;
  $('profilePreset').value = saved.v === PREFS_VERSION && preset in PRESETS ? preset
    : saved.v === PREFS_VERSION && saved.height !== undefined ? '' : 'recommended';
  if ($('profilePreset').value === 'recommended') applySettings(PRESETS.recommended);
  updateAdvancedSummary();
  return saved;
}

// Resumo ao lado de "Configurações avançadas", para ver o essencial sem abrir.
function updateAdvancedSummary() {
  const s = readSettings();
  $('advancedSummary').textContent =
    `${s.height ? s.height + 'p' : 'resolução original'} · ${s.fps} fps · ${(s.bitrateKbps / 1000).toLocaleString('pt-BR')} Mbps`;
}

$('profilePreset').onchange = () => {
  const p = PRESETS[$('profilePreset').value];
  if (p) applySettings(p);
  else setAdvancedOpen(true);   // "Personalizado": abre as configurações para ajustar
  updateAdvancedSummary();
  savePrefs();
};
for (const el of settingEls()) {
  el.addEventListener('change', () => {
    $('profilePreset').value = '';   // mexeu em qualquer configuração avançada: passa a ser "Personalizado"
    updateAdvancedSummary();
    savePrefs();
  });
}

function setAdvancedOpen(open, save = true) {
  $('advanced').hidden = !open;
  $('advancedBtn').classList.toggle('active', open);
  $('advancedBtn').setAttribute('aria-expanded', String(open));
  if (save) { try { localStorage.setItem('gamestream-advanced-open', open ? '1' : '0'); } catch { /* sem armazenamento */ } }
}
$('advancedBtn').onclick = () => setAdvancedOpen($('advanced').hidden);
try { setAdvancedOpen(localStorage.getItem('gamestream-advanced-open') === '1', false); } catch { /* sem armazenamento */ }

// ---------- Janelas e monitores (com miniaturas) ----------

let pickerKind = 'window';
const thumbUrl = (id) => `${api('thumb')}&id=${encodeURIComponent(id)}&_=${Date.now()}`;

async function loadSources(saved) {
  try {
    sources = await (await fetch(api('sources'))).json();
  } catch {
    return;
  }
  // Mantém a seleção atual; senão tenta a janela usada da última vez (o identificador muda a cada execução).
  const current = sources.find((x) => x.id === $('source').value);
  const previous = saved && (sources.find((x) => x.name === saved.sourceName && x.detail === saved.sourceDetail)
    || sources.find((x) => x.detail === saved.sourceDetail && x.kind === 'window'));
  selectSource(current || previous || null, false);
}

function selectSource(src, save = true) {
  $('source').value = src ? src.id : '';
  $('sourceName').textContent = src ? src.name : 'Escolher janela ou monitor…';
  $('sourceDetail').textContent = src ? `${src.detail} · ${src.width}×${src.height}` : '';
  const img = $('sourceThumb');
  if (src) {
    img.onerror = () => { img.hidden = true; };
    img.onload = () => { img.hidden = false; };
    img.src = thumbUrl(src.id);
  } else {
    img.hidden = true;
  }
  if (save) savePrefs();
}

function renderPicker() {
  for (const t of document.querySelectorAll('.tab')) t.classList.toggle('active', t.dataset.kind === pickerKind);
  const grid = $('pickerGrid');
  grid.innerHTML = '';
  const list = sources.filter((x) => x.kind === pickerKind);
  if (!list.length) {
    grid.innerHTML = '<div class="muted">Nada encontrado.</div>';
    return;
  }
  for (const s of list) {
    const card = document.createElement('button');
    card.className = 'card' + (s.id === $('source').value ? ' selected' : '');
    card.title = s.name;
    const thumb = document.createElement('div');
    thumb.className = 'thumb';
    thumb.textContent = 'carregando…';
    const img = new Image();
    img.onload = () => { thumb.textContent = ''; thumb.append(img); };
    img.onerror = () => { thumb.textContent = 'sem prévia (minimizada?)'; };
    img.src = thumbUrl(s.id);
    const name = document.createElement('b');
    name.textContent = s.name;
    const detail = document.createElement('small');
    detail.textContent = `${s.detail} · ${s.width}×${s.height}`;
    card.append(thumb, name, detail);
    card.onclick = () => {
      selectSource(s);
      $('picker').close();
    };
    grid.append(card);
  }
}

async function openPicker() {
  const current = sources.find((x) => x.id === $('source').value);
  if (current) pickerKind = current.kind;
  $('picker').showModal();
  renderPicker();          // mostra a lista atual já
  await loadSources();     // e atualiza (janelas novas/fechadas)
  renderPicker();
}

$('sourceBtn').onclick = openPicker;
$('pickerClose').onclick = () => $('picker').close();
$('pickerRefresh').onclick = async () => { await loadSources(); renderPicker(); };
for (const t of document.querySelectorAll('.tab')) {
  t.onclick = () => { pickerKind = t.dataset.kind; renderPicker(); };
}
$('picker').addEventListener('click', (e) => { if (e.target === $('picker')) $('picker').close(); });

// ---------- Medidor de áudio ----------

async function updateMeter() {
  let db = null;
  let autoGain = null;
  try {
    const level = await (await fetch(api('level'))).json();
    db = level.db;
    autoGain = level.autoGainDb;
  } catch { /* app fechando */ }
  const fill = $('meterFill');
  if (db === null || db === undefined) {
    fill.style.width = '0';
    $('meterDb').textContent = '—';
    return;
  }
  // Escala de -60 dB (vazio) a 0 dB (cheio).
  const pct = Math.max(0, Math.min(100, ((db + 60) / 60) * 100));
  fill.style.width = pct + '%';
  fill.className = db > -3 ? 'clip' : db > -12 ? 'hot' : '';
  $('meterDb').textContent = db <= -99 ? 'silêncio' : `${db.toFixed(0)} dB`;
  // No automático, mostra quanto o app está aumentando o volume.
  $('meterDb').title = autoGain === null || autoGain === undefined ? '' : `Ganho automático: +${autoGain} dB`;
}

// ---------- Início / parada ----------

// O servidor recebe o ganho como número + um "automático" à parte.
function settingsForServer() {
  const s = { source: $('source').value, ...readSettings() };
  s.audioAutoGain = s.audioGainDb === 'auto';
  if (s.audioAutoGain) s.audioGainDb = 0;
  return s;
}

async function start() {
  if (!$('source').value) {
    showMessage('Escolha uma janela ou monitor.', 'error');
    return;
  }
  savePrefs();
  $('startBtn').disabled = true;
  await fetch(api('start'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settingsForServer()),
  });
  refresh();
}

async function stop() {
  await fetch(api('stop'), { method: 'POST' });
  refresh();
}

// ---------- Status ----------

function showMessage(text, kind = 'muted') {
  $('message').textContent = text || '';
  $('message').className = kind;
  $('message').hidden = !text;   // sem mensagem, não ocupa espaço
}

async function refresh() {
  let s;
  try {
    s = await (await fetch(api('status'))).json();
  } catch {
    return;
  }

  const live = s.state === 'live';
  const busy = s.state === 'starting';
  $('startBtn').hidden = live;
  $('startBtn').disabled = busy;
  $('startBtn').textContent = busy ? '⏳ Iniciando…' : '▶ Iniciar transmissão';
  $('stopBtn').hidden = !live;
  for (const el of [...settingEls(), $('sourceBtn'), $('profilePreset')]) el.disabled = live || busy;
  $('liveBadge').classList.toggle('live', live);
  $('liveText').textContent = live ? 'AO VIVO' : 'OFFLINE';
  showMessage(s.message, s.state === 'error' ? 'error' : live ? 'warn' : 'muted');

  $('viewerCount').textContent = s.maxViewers ? `${s.viewers} / ${s.maxViewers}` : s.viewers;
  $('guestStreams').textContent = s.guestStreams;
  if (document.activeElement !== $('maxViewers')) $('maxViewers').value = String(s.maxViewers);
  $('sourceOut').textContent = live ? s.source : '—';
  $('sourceOut').title = s.source || '';
  $('sizeOut').textContent = live ? s.outputSize : '—';
  $('fpsOut').textContent = live ? `${s.fps} fps` : '—';
  $('kbpsOut').textContent = live ? `${(s.kbps / 1000).toFixed(1)} Mbps` : '—';
  $('encoderOut').textContent = live ? s.encoder : '—';
  $('audioOut').textContent = live ? s.audio : '—';

  // A sala: a sua (você como anfitrião) ou a do amigo em que você entrou.
  joined = s.joined;
  lastStatus = s;
  if (joined?.hostName && joinedWith) rememberRoom(joinedWith, joined.hostName);   // dá nome à sala na lista de recentes
  const wanted = joined ? joined.roomUrl : s.roomUrl + (myName() ? `&name=${encodeURIComponent(myName())}` : '');
  if (wanted !== roomSrc) {
    roomSrc = wanted;
    $('room').src = wanted;
  }
  const inRoom = !!joined || s.allowShare;
  if (!busy) $('startBtn').textContent = inRoom ? '▶ Compartilhar minha tela na sala' : '▶ Iniciar transmissão';
  $('stopBtn').textContent = inRoom ? '■ Parar de compartilhar' : '■ Parar';
  renderView();
  if (!rotating) $('rotateBtn').disabled = !s.publicUrl;

  if (s.publicUrl !== shownUrl || !s.publicUrl) {
    shownUrl = s.publicUrl;
    renderLink(s);
  }
  renderCode(s);
}

function renderLink(s) {
  const box = $('links');
  box.innerHTML = '';
  if (!s.publicUrl) {
    const status = document.createElement('div');
    status.className = 'muted';
    status.style.fontSize = '12.5px';
    status.textContent = '⏳ ' + s.tunnelStatus;
    box.append(status);
    return;
  }
  const row = document.createElement('div');
  row.className = 'link';
  const code = document.createElement('code');
  code.textContent = code.title = s.publicUrl;
  const btn = document.createElement('button');
  btn.textContent = 'Copiar';
  btn.onclick = async () => {
    await navigator.clipboard.writeText(s.publicUrl);
    btn.textContent = 'Copiado!';
    setTimeout(() => (btn.textContent = 'Copiar'), 1500);
  };
  row.append(code, btn);
  box.append(row);
}

// ---------- Navegação: Transmissão e Salas ----------
//
// "Transmissão" (tela inicial): só você transmite; quem tem o código ou o link assiste.
// "Salas": criar uma sala (todos podem compartilhar) ou entrar na de um amigo.
// No servidor, a diferença entre as duas é só se os convidados podem compartilhar a tela.

let view = 'broadcast';   // 'broadcast' | 'rooms'
let showList = false;     // na área de salas, ver a lista mesmo estando numa sala (ela continua aberta)
let lastStatus = null;
let joinedWith = '';      // código/link usado para entrar na sala atual (para a lista de recentes)

function myName() {
  return $('myName').value.trim().slice(0, 24);
}

$('myName').onchange = () => {
  try { localStorage.setItem('gamestream-name', myName()); } catch { /* sem armazenamento */ }
};

/** Estou numa sala que eu mesmo criei? */
const inOwnRoom = () => !!lastStatus?.allowShare && !joined;

function renderView() {
  const own = inOwnRoom();
  if ((joined || own) && view !== 'rooms') view = 'rooms';   // quem está numa sala está na área de salas
  const lobby = view === 'rooms' && (showList || (!joined && !own));

  $('navBroadcast').classList.toggle('active', view === 'broadcast');
  $('navRooms').classList.toggle('active', view === 'rooms');
  $('lobby').hidden = !lobby;
  $('workspace').hidden = lobby;
  document.body.classList.toggle('no-side', lobby);
  $('liveBadge').hidden = lobby;

  $('pageTitle').textContent = view === 'broadcast' ? 'Transmissão'
    : lobby ? 'Salas'
    : joined ? `Sala de ${joined.hostName || 'um amigo'}`
    : (activeRoom()?.name || 'Minha sala');
  $('backToRoomsBtn').hidden = lobby || !(joined || own);
  $('roomActionBtn').hidden = lobby || !(joined || own);
  $('roomActionBtn').textContent = joined ? 'Sair da sala' : 'Encerrar sala';

  // Painel lateral: código e link (transmissão ou sala própria) ou o aviso de convidado.
  $('hostSection').hidden = !!joined || lobby;
  $('guestSection').hidden = !joined;
  $('roomStats').hidden = !!joined || lobby;
  $('codeTitle').textContent = own ? '🔑 Código da sala' : '🔑 Código para assistir';
  $('viewerLabel').textContent = own ? 'Pessoas na sala' : 'Pessoas assistindo';
  $('guestStreamsRow').hidden = !own;
  $('modeHint').textContent = own
    ? 'Quem entra com o código ou o link assiste e também pode compartilhar a própria tela. Ninguém controla seu PC por ele.'
    : 'Quem tem o código ou o link só assiste à sua transmissão. Para todos poderem compartilhar, crie uma sala em Salas.';
  $('myName').disabled = !!joined;   // o nome vai junto ao entrar; para mudar, saia e entre de novo

  const box = $('joinMessage');
  if (joined) {
    const connected = joined.state === 'connected';
    box.hidden = connected && !joined.message;
    box.className = 'error';
    box.textContent = joined.message || 'Conectando à sala…';
  } else {
    box.hidden = true;
  }
  if (lobby) {
    renderMyRooms();
    renderRecent();
  }
}

$('navRooms').onclick = () => { view = 'rooms'; showList = false; renderView(); };
$('backToRoomsBtn').onclick = () => { showList = true; renderView(); };
$('navBroadcast').onclick = async () => {
  if (joined) {
    if (!confirm('Sair da sala e voltar para a sua transmissão?')) return;
    await leaveRoom();
  } else if (inOwnRoom() && !(await endOwnRoom())) {
    return;
  }
  view = 'broadcast';
  renderView();
};

// ---------- Minhas salas ----------
// Uma sala salva é um nome que você reabre quando quiser. O código muda a cada abertura
// (o endereço da sala é sorteado de novo), então os amigos recebem o código do dia.

function myRooms() {
  try { return JSON.parse(localStorage.getItem('gamestream-my-rooms') || '[]'); } catch { return []; }
}

function storeMyRooms(list) {
  try { localStorage.setItem('gamestream-my-rooms', JSON.stringify(list.slice(0, 20))); } catch { /* sem armazenamento */ }
}

let activeRoomId = null;   // sala salva que está aberta agora (enquanto o servidor estiver em modo sala)

/** A sala salva que está aberta agora, se houver. */
function activeRoom() {
  return inOwnRoom() ? myRooms().find((r) => r.id === activeRoomId) || null : null;
}

async function openRoom(room) {
  // Abrir uma sala gera um código novo: quem estava assistindo ou em outra sala precisa dele para entrar.
  const others = lastStatus?.viewers > 0;
  if (others && !confirm(`Abrir "${room.name}" gera um código novo: quem está conectado agora será desconectado. Continuar?`)) return;
  await fetch(api('rotate-key'), { method: 'POST' });
  await fetch(`${api('allow-share')}&on=1&name=${encodeURIComponent(room.name)}`, { method: 'POST' });
  activeRoomId = room.id;
  try { sessionStorage.setItem('gamestream-active-room', room.id); } catch { /* sem armazenamento */ }
  storeMyRooms(myRooms().map((r) => (r.id === room.id ? { ...r, at: Date.now() } : r)));
  showList = false;
  await refresh();
}

async function createRoom() {
  const name = $('newRoomName').value.trim().slice(0, 32) || `Sala ${myRooms().length + 1}`;
  const room = { id: Math.random().toString(36).slice(2, 10), name, at: Date.now() };
  storeMyRooms([room, ...myRooms()]);
  $('newRoomName').value = '';
  $('createRoomBtn').disabled = true;
  try { await openRoom(room); } finally { $('createRoomBtn').disabled = false; }
  renderView();
}

/** Fecha a sala aberta (ela continua salva em "Minhas salas"). */
async function endOwnRoom() {
  const name = activeRoom()?.name || 'a sala';
  if (!confirm(`Encerrar "${name}"? Todos são desconectados e o código deixa de valer. A sala continua salva para você abrir de novo depois.`)) return false;
  await fetch(`${api('allow-share')}&on=0`, { method: 'POST' });
  await fetch(api('rotate-key'), { method: 'POST' });
  activeRoomId = null;
  try { sessionStorage.removeItem('gamestream-active-room'); } catch { /* sem armazenamento */ }
  showList = false;
  await refresh();
  return true;
}

function renderMyRooms() {
  const box = $('myRooms');
  box.textContent = '';
  const open = activeRoom();
  for (const r of myRooms()) {
    const row = document.createElement('div');
    row.className = 'recent';
    const info = document.createElement('div');
    info.className = 'info';
    const name = document.createElement('b');
    name.textContent = r.name;
    const when = document.createElement('small');
    if (open?.id === r.id) {
      when.className = 'open-tag';
      when.textContent = '● Aberta agora';
    } else {
      when.textContent = 'Aberta pela última vez em ' + new Date(r.at).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
    }
    info.append(name, when);

    const go = document.createElement('button');
    if (open?.id === r.id) {
      go.textContent = 'Voltar para a sala';
      go.className = 'primary';
      go.onclick = () => { showList = false; renderView(); };
    } else {
      go.textContent = 'Abrir';
      go.onclick = async () => {
        if (joined) {
          if (!confirm('Sair da sala do seu amigo para abrir a sua?')) return;
          await leaveRoom();
        }
        await openRoom(r);
      };
    }
    const remove = document.createElement('button');
    remove.className = 'small-btn';
    remove.textContent = '✕';
    remove.title = 'Apagar esta sala da lista';
    remove.disabled = open?.id === r.id;   // encerre a sala antes de apagá-la
    remove.onclick = () => {
      if (!confirm(`Apagar "${r.name}" da lista?`)) return;
      storeMyRooms(myRooms().filter((x) => x.id !== r.id));
      renderMyRooms();
    };
    row.append(info, go, remove);
    box.append(row);
  }
}

$('createRoomBtn').onclick = createRoom;
$('newRoomName').onkeydown = (e) => { if (e.key === 'Enter') createRoom(); };
$('roomActionBtn').onclick = async () => {
  if (joined) await leaveRoom();
  else await endOwnRoom();
};

// ---------- Entrar na sala de um amigo ----------

function showJoinError(text) {
  $('joinError').textContent = text || '';
  $('joinError').hidden = !text;
}

async function joinRoom(code) {
  const url = (code ?? $('joinUrl').value).trim();
  if (!url) return;
  if (!myName()) {
    $('myName').focus();
    showJoinError('Escreva o seu nome (no menu à esquerda) antes de entrar.');
    return;
  }
  $('joinBtn').disabled = true;
  try {
    const res = await fetch(api('join'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url, name: myName() }),
    });
    if (!res.ok) {
      showJoinError((await res.json().catch(() => ({}))).error || 'Não foi possível entrar na sala.');
      return;
    }
    showJoinError('');
    showList = false;
    joinedWith = url;
    rememberRoom(url, '');
    $('joinUrl').value = '';
    await refresh();
  } finally {
    $('joinBtn').disabled = false;
  }
}

async function leaveRoom() {
  await fetch(api('leave'), { method: 'POST' });
  joined = null;
  joinedWith = '';
  await refresh();
}

$('joinBtn').onclick = () => joinRoom();
$('joinUrl').onkeydown = (e) => { if (e.key === 'Enter') joinRoom(); };

// ---------- Salas recentes ----------
// Só ajudam enquanto a sala continuar aberta: o código muda quando quem a criou fecha o app.

function recentRooms() {
  try { return JSON.parse(localStorage.getItem('gamestream-rooms') || '[]'); } catch { return []; }
}

function storeRooms(list) {
  try { localStorage.setItem('gamestream-rooms', JSON.stringify(list.slice(0, 8))); } catch { /* sem armazenamento */ }
}

function rememberRoom(code, label) {
  const old = recentRooms().find((r) => r.code === code);
  storeRooms([{ code, label: label || old?.label || '', at: Date.now() }, ...recentRooms().filter((r) => r.code !== code)]);
}

function renderRecent() {
  const list = recentRooms();
  $('recentCard').hidden = !list.length;
  const box = $('recentRooms');
  box.textContent = '';
  for (const r of list) {
    const row = document.createElement('div');
    row.className = 'recent';
    const info = document.createElement('div');
    info.className = 'info';
    const name = document.createElement('b');
    name.textContent = r.label ? `Sala de ${r.label}` : 'Sala';
    const code = document.createElement('code');
    code.textContent = r.code.length > 60 ? r.code.slice(0, 57) + '…' : r.code;
    const when = document.createElement('small');
    when.textContent = 'Usada em ' + new Date(r.at).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
    info.append(name, code, when);
    const enter = document.createElement('button');
    enter.textContent = 'Entrar';
    enter.onclick = () => joinRoom(r.code);
    const remove = document.createElement('button');
    remove.className = 'small-btn';
    remove.textContent = '✕';
    remove.title = 'Tirar da lista';
    remove.onclick = () => { storeRooms(recentRooms().filter((x) => x.code !== r.code)); renderRecent(); };
    row.append(info, enter, remove);
    box.append(row);
  }
}

// ---------- Código da sala ----------

let shownCode;

function renderCode(s) {
  if (s.code === shownCode) return;
  shownCode = s.code;
  $('roomCode').textContent = s.code || 'gerando…';
  $('roomCode').classList.toggle('muted', !s.code);
  $('codeCopy').disabled = !s.code;
}

$('codeCopy').onclick = async () => {
  if (!shownCode) return;
  await navigator.clipboard.writeText(shownCode);
  $('codeCopy').textContent = 'Copiado!';
  setTimeout(() => ($('codeCopy').textContent = 'Copiar'), 1500);
};

// ---------- Painel lateral recolhível ----------

function setSideCollapsed(collapsed, save = true) {
  document.body.classList.toggle('side-collapsed', collapsed);
  $('sideShow').hidden = !collapsed;
  if (save) { try { localStorage.setItem('gamestream-side-collapsed', collapsed ? '1' : '0'); } catch { /* sem armazenamento */ } }
}
$('sideHide').onclick = () => setSideCollapsed(true);
$('sideShow').onclick = () => setSideCollapsed(false);
try { setSideCollapsed(localStorage.getItem('gamestream-side-collapsed') === '1', false); } catch { /* sem armazenamento */ }

// ---------- Controle do link ----------

let rotating = false;
$('rotateBtn').onclick = async () => {
  if (!confirm('Gerar um código e um link novos? Todos são desconectados e precisarão do código ou link novo.')) return;
  rotating = true;
  $('rotateBtn').disabled = true;
  await fetch(api('rotate-key'), { method: 'POST' });
  rotating = false;
  refresh();
};
$('maxViewers').onchange = () => {
  try { localStorage.setItem('gamestream-max-viewers', $('maxViewers').value); } catch { /* sem armazenamento */ }
  fetch(`${api('max-viewers')}&n=${$('maxViewers').value}`, { method: 'POST' });
};
$('logBtn').onclick = () => fetch(api('open-log'), { method: 'POST' });

// ---------- Codificadores disponíveis neste PC ----------

async function loadEncoders() {
  let list;
  try {
    const res = await fetch(api('encoders'));
    if (!res.ok) return;
    list = await res.json();
  } catch {
    return;
  }
  for (const e of list) {
    const opt = $('encoder').querySelector(`option[value="${e.id}"]`);
    if (opt) opt.textContent = e.available ? e.label : `${e.label} — indisponível neste PC`;
  }
  const gpus = list.filter((e) => e.available && e.id !== 'x264').map((e) => e.label);
  $('encoder').querySelector('option[value="auto"]').textContent =
    gpus.length ? `Automático (${gpus[0]})` : 'Automático (CPU — sem GPU compatível)';
}

// ---------- Inicialização ----------

$('startBtn').onclick = start;
$('stopBtn').onclick = stop;
const saved = loadPrefs();
try {
  $('myName').value = localStorage.getItem('gamestream-name') || '';
  activeRoomId = sessionStorage.getItem('gamestream-active-room');
} catch { /* sem armazenamento */ }
loadSources(saved);
loadEncoders();
// O limite de pessoas fica no servidor; reaplica o que foi escolhido da última vez.
try {
  const max = localStorage.getItem('gamestream-max-viewers');
  if (max !== null) fetch(`${api('max-viewers')}&n=${max}`, { method: 'POST' });
} catch { /* sem armazenamento */ }
refresh();
setInterval(refresh, 1000);
setInterval(updateMeter, 150);
