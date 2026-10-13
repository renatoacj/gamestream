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
let mode = 'host';      // 'host' = criar sala | 'join' = entrar na sala de alguém
let joined = null;      // dados da sala em que entramos (vem do status)
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
  const encoder = $('encoder').selectedOptions[0]?.textContent.split(' —')[0] || '';
  $('advancedSummary').textContent =
    `${s.height ? s.height + 'p' : 'resolução original'} · ${s.fps} fps · ${(s.bitrateKbps / 1000).toLocaleString('pt-BR')} Mbps · ${encoder}`;
}

$('profilePreset').onchange = () => {
  const p = PRESETS[$('profilePreset').value];
  if (p) applySettings(p);
  else $('advanced').open = true;   // "Personalizado": abre as configurações para ajustar
  updateAdvancedSummary();
  savePrefs();
};
for (const el of settingEls()) {
  el.addEventListener('change', () => {
    // Mexeu em vídeo/codificação: passa a ser "Personalizado" (áudio não faz parte das predefinições).
    if (!el.id.startsWith('audio')) $('profilePreset').value = '';
    updateAdvancedSummary();
    savePrefs();
  });
}
$('advanced').addEventListener('toggle', () => {
  try { localStorage.setItem('gamestream-advanced-open', $('advanced').open ? '1' : '0'); } catch { /* sem armazenamento */ }
});
try { $('advanced').open = localStorage.getItem('gamestream-advanced-open') === '1'; } catch { /* sem armazenamento */ }

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
  $('allowShare').checked = s.allowShare;
  $('sourceOut').textContent = live ? s.source : '—';
  $('sourceOut').title = s.source || '';
  $('sizeOut').textContent = live ? s.outputSize : '—';
  $('fpsOut').textContent = live ? `${s.fps} fps` : '—';
  $('kbpsOut').textContent = live ? `${(s.kbps / 1000).toFixed(1)} Mbps` : '—';
  $('encoderOut').textContent = live ? s.encoder : '—';
  $('audioOut').textContent = live ? s.audio : '—';

  // A sala: a sua (você como anfitrião) ou a do amigo em que você entrou.
  joined = s.joined;
  if (joined && mode !== 'join') setMode('join', false);
  const wanted = joined ? joined.roomUrl : s.roomUrl + (myName() ? `&name=${encodeURIComponent(myName())}` : '');
  if (wanted !== roomSrc) {
    roomSrc = wanted;
    $('room').src = wanted;
  }
  renderJoinState();
  if (!busy) $('startBtn').textContent = joined ? '▶ Compartilhar minha tela na sala' : '▶ Iniciar transmissão';
  $('stopBtn').textContent = joined ? '■ Parar de compartilhar' : '■ Parar';
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

// ---------- Nome e modo (criar sala / entrar em uma sala) ----------

function myName() {
  return $('myName').value.trim().slice(0, 24);
}

$('myName').onchange = () => {
  try { localStorage.setItem('gamestream-name', myName()); } catch { /* sem armazenamento */ }
};

function setMode(next, save = true) {
  mode = next;
  $('modeHost').classList.toggle('active', mode === 'host');
  $('modeJoin').classList.toggle('active', mode === 'join');
  $('joinRow').hidden = mode !== 'join';
  $('hostSection').hidden = mode === 'join';
  $('roomStats').hidden = mode === 'join';   // esses números são da sua sala, não da do amigo
  $('guestSection').hidden = mode !== 'join';
  if (save) { try { localStorage.setItem('gamestream-mode', mode); } catch { /* sem armazenamento */ } }
  renderJoinState();
}

function renderJoinState() {
  const inRoom = !!joined;
  $('joinBtn').hidden = inRoom;
  $('leaveBtn').hidden = !inRoom;
  $('joinUrl').disabled = inRoom;
  $('myName').disabled = inRoom;   // o nome vai junto ao entrar; para mudar, saia e entre de novo
  const box = $('joinMessage');
  if (mode !== 'join') { box.hidden = true; return; }
  if (inRoom) {
    const connected = joined.state === 'connected';
    box.hidden = false;
    box.className = connected && !joined.message ? 'ok' : 'error';
    box.textContent = joined.message || (connected ? '✔ Conectado à sala.' : 'Conectando à sala…');
  } else if (!joinError) {
    box.hidden = true;
  }
}

let joinError = '';

async function joinRoom() {
  const url = $('joinUrl').value.trim();
  if (!url) return;
  if (!myName()) {
    $('myName').focus();
    showJoinError('Escreva o seu nome antes de entrar.');
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
    joinError = '';
    try { localStorage.setItem('gamestream-last-room', url); } catch { /* sem armazenamento */ }
    await refresh();
  } finally {
    $('joinBtn').disabled = false;
  }
}

function showJoinError(text) {
  joinError = text;
  $('joinMessage').hidden = false;
  $('joinMessage').className = 'error';
  $('joinMessage').textContent = text;
}

async function leaveRoom() {
  await fetch(api('leave'), { method: 'POST' });
  joined = null;
  await refresh();
}

$('joinBtn').onclick = joinRoom;
$('joinUrl').onkeydown = (e) => { if (e.key === 'Enter') joinRoom(); };
$('leaveBtn').onclick = leaveRoom;
$('modeJoin').onclick = () => setMode('join');
$('modeHost').onclick = async () => {
  if (joined && !confirm('Sair da sala do seu amigo e voltar para a sua?')) return;
  if (joined) await leaveRoom();
  setMode('host');
};

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
  if (!confirm('Gerar um link e um código novos? Todos que estão na sala serão desconectados e precisarão do código ou link novo.')) return;
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
$('allowShare').onchange = () => {
  try { localStorage.setItem('gamestream-allow-share', $('allowShare').checked ? '1' : '0'); } catch { /* sem armazenamento */ }
  fetch(`${api('allow-share')}&on=${$('allowShare').checked ? 1 : 0}`, { method: 'POST' });
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
  $('joinUrl').value = localStorage.getItem('gamestream-last-room') || '';
  setMode(localStorage.getItem('gamestream-mode') === 'join' ? 'join' : 'host', false);
} catch { /* sem armazenamento */ }
loadSources(saved);
loadEncoders();
// Limite e permissão de compartilhar ficam no servidor; reaplica o que foi escolhido da última vez.
try {
  const max = localStorage.getItem('gamestream-max-viewers');
  if (max !== null) fetch(`${api('max-viewers')}&n=${max}`, { method: 'POST' });
  const share = localStorage.getItem('gamestream-allow-share');
  if (share !== null) fetch(`${api('allow-share')}&on=${share}`, { method: 'POST' });
} catch { /* sem armazenamento */ }
refresh();
setInterval(refresh, 1000);
setInterval(updateMeter, 150);
