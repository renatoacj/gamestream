'use strict';

// Interface do app. A captura e a codificação rodam no C# (FFmpeg + WASAPI);
// esta página só envia comandos e mostra o status.

const HOST_TOKEN = new URLSearchParams(location.search).get('t');
const $ = (id) => document.getElementById(id);
const api = (path) => `/api/${path}?t=${HOST_TOKEN}`;

// ---------- Dados guardados pelo app ----------
// Salas, salas recentes, qualidade e painéis ficam no arquivo do app (app-settings.json). O armazenamento do
// navegador embutido sozinho não basta: ele muda com a porta do app (8790, 8791…) e pode perder o que foi gravado
// logo antes de o app fechar. Ao abrir, a página recebe do app o que estava guardado (antes de ler qualquer coisa);
// cada gravação vai para os dois lugares.
const STORE_KEYS = ['gamestream-settings', 'gamestream-name', 'gamestream-my-rooms', 'gamestream-rooms',
  'gamestream-nav-collapsed', 'gamestream-side-collapsed', 'gamestream-max-viewers'];
let storePending = {};
let storeTimer = null;

function flushStore() {
  const items = storePending;
  storePending = {};
  if (!Object.keys(items).length) return;
  fetch(api('store'), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(items), keepalive: true })
    .catch(() => {});
}

// Cópia em memória (a leitura vem daqui): o armazenamento do navegador embutido já devolveu listas vazias no meio
// do uso, e o app regravava a lista com um item só — as salas "sumiam".
const storeCache = {};

/** Lê um dado guardado (da cópia em memória; o navegador só se ela não tiver). */
function readItem(key) {
  if (key in storeCache) return storeCache[key];
  try { return localStorage.getItem(key); } catch { return null; }
}

/** Grava no navegador e no app. */
function persistItem(key, value) {
  storeCache[key] = String(value);
  try { localStorage.setItem(key, value); } catch { /* sem armazenamento */ }
  storePending[key] = String(value);
  clearTimeout(storeTimer);
  storeTimer = setTimeout(flushStore, 300);
}

(function restoreStore() {
  let saved = {};
  try {
    // Síncrono de propósito: o resto da página lê estes dados logo em seguida. É o próprio app, na mesma máquina.
    const req = new XMLHttpRequest();
    req.open('GET', api('store'), false);
    req.send();
    if (req.status === 200) saved = JSON.parse(req.responseText) || {};
  } catch { /* app antigo ou fechando */ }
  for (const key of STORE_KEYS) {
    if (typeof saved[key] === 'string') {
      storeCache[key] = saved[key];
      try { localStorage.setItem(key, saved[key]); } catch { /* sem armazenamento */ }
    } else {
      // Gravado por uma versão antiga, só no navegador: passa a ficar no app também.
      let value = null;
      try { value = localStorage.getItem(key); } catch { /* sem armazenamento */ }
      if (value !== null) {
        storeCache[key] = value;
        storePending[key] = value;
      }
    }
  }
  flushStore();
})();
window.addEventListener('pagehide', flushStore);

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
    persistItem('gamestream-settings', JSON.stringify({
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
  try { saved = JSON.parse(readItem('gamestream-settings') || '{}'); } catch { /* nada salvo */ }
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

// ---------- Opções do app (bandeja e iniciar com o Windows) ----------

async function loadAppSettings() {
  try {
    const s = await (await fetch(api('app-settings'))).json();
    $('optTray').checked = s.closeToTray;
    $('optAutostart').checked = s.startWithWindows;
    $('optFixedCodes').checked = s.fixedRoomCodes;
    $('optUpdates').checked = s.checkUpdates;
  } catch { /* app fechando */ }
}

async function saveAppSettings() {
  const res = await fetch(api('app-settings'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      closeToTray: $('optTray').checked,
      startWithWindows: $('optAutostart').checked,
      fixedRoomCodes: $('optFixedCodes').checked,
      checkUpdates: $('optUpdates').checked,
    }),
  }).catch(() => null);
  const error = res && !res.ok ? (await res.json().catch(() => ({}))).error : null;
  $('appSettingsError').textContent = error || '';
  $('appSettingsError').hidden = !error;
  loadAppSettings();   // mostra o estado real (ex.: se o Windows recusou a mudança)
}
$('optTray').onchange = saveAppSettings;
$('optAutostart').onchange = saveAppSettings;
$('optFixedCodes').onchange = saveAppSettings;
$('optUpdates').onchange = saveAppSettings;
// O link da política abre no navegador padrão (a janela do app não navega para fora).
$('privacyLink').onclick = (e) => { e.preventDefault(); fetch(api('open-privacy'), { method: 'POST' }); };

// ---------- Aviso de versão nova ----------
let updateDismissed = '';
function renderUpdate(s) {
  const v = s?.update?.version;
  const show = !!v && v !== updateDismissed;
  $('updateBanner').hidden = !show;
  if (show) $('updateText').textContent = `Nova versão do GameStream disponível: ${v} (você está na ${s.version}).`;
}
$('updateBtn').onclick = () => fetch(api('open-update'), { method: 'POST' });
$('updateClose').onclick = () => { updateDismissed = lastStatus?.update?.version || ''; renderUpdate(lastStatus); };

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
      const changed = s.id !== $('source').value;
      selectSource(s);
      $('picker').close();
      if (changed && lastStatus?.state === 'live') switchSource();
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
  // O medidor só aparece em Configurações: fora dela (ou com a janela escondida) não pergunta nada ao app.
  if ($('settingsPage').hidden || document.hidden) return;
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

/** Troca a janela/monitor durante a transmissão: quem assiste só vê a imagem mudar. */
async function switchSource() {
  savePrefs();
  await fetch(`${api('start')}&switch=1`, {
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

// ---------- Peças da interface ----------

/** Ícone + texto num botão, sem redesenhar quando nada mudou (o status é relido a cada segundo). */
function setButton(el, iconName, text) {
  const key = iconName + '|' + text;
  if (el.dataset.label === key) return;
  el.dataset.label = key;
  setLabel(el, iconName, text);
}

/** Pergunta de confirmação na janela do próprio app. Resolve com true se a pessoa confirmar. */
function ask(title, text, okLabel = 'Confirmar', danger = false) {
  const dlg = $('askDialog');
  $('askTitle').textContent = title;
  $('askText').textContent = text;
  $('askOk').textContent = okLabel;
  $('askOk').className = danger ? 'danger' : 'primary';
  return new Promise((resolve) => {
    let answer = false;
    $('askOk').onclick = () => { answer = true; dlg.close(); };
    $('askCancel').onclick = () => dlg.close();
    dlg.onclose = () => resolve(answer);
    dlg.showModal();
  });
}

function initials(name) {
  const words = name.trim().split(/\s+/).filter(Boolean);
  const text = words.length > 1 ? words[0][0] + words[words.length - 1][0] : (words[0] || '?').slice(0, 2);
  return text.toUpperCase();
}

/** Quadradinho colorido com as iniciais; a cor sai do nome, então é sempre a mesma para a mesma sala. */
function avatar(name) {
  let hash = 0;
  for (const ch of name) hash = (hash * 31 + ch.codePointAt(0)) >>> 0;
  const el = document.createElement('div');
  el.className = 'avatar';
  el.style.background = `linear-gradient(135deg, hsl(${hash % 360} 55% 46%), hsl(${(hash + 40) % 360} 55% 34%))`;
  el.textContent = initials(name);
  return el;
}

const shortDate = (at) => new Date(at).toLocaleString('pt-BR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });

/** Card de sala: avatar, nome, linha de detalhe, botão de apagar (some até passar o mouse) e a ação principal. */
function roomCard({ name, detail, detailClass, open, action, onAction, primary, removeTitle, onRemove, removeDisabled }) {
  const card = document.createElement('div');
  card.className = 'room-card' + (open ? ' open' : '');
  const top = document.createElement('div');
  top.className = 'top';
  const info = document.createElement('div');
  info.className = 'info';
  const title = document.createElement('b');
  title.textContent = title.title = name;
  const sub = document.createElement('small');
  sub.className = detailClass || '';
  sub.textContent = sub.title = detail;
  info.append(title, sub);
  const remove = document.createElement('button');
  remove.className = 'remove icon-btn ghost';
  remove.append(icon('trash'));
  remove.title = removeTitle;
  remove.disabled = !!removeDisabled;
  remove.onclick = onRemove;
  top.append(avatar(name), info, remove);
  const go = document.createElement('button');
  go.className = 'go' + (primary ? ' primary' : '');
  go.textContent = action;
  go.onclick = onAction;
  card.append(top, go);
  return card;
}

// ---------- Status ----------

function showMessage(text, kind = 'muted') {
  $('message').textContent = text || '';
  $('message').className = kind;
  $('message').hidden = !text;   // sem mensagem, não ocupa espaço
}

let refreshSeq = 0;
let refreshApplied = 0;

async function refresh() {
  let s;
  const seq = ++refreshSeq;
  try {
    s = await (await fetch(api('status'))).json();
  } catch {
    return;
  }
  // Respostas podem chegar fora de ordem (a leitura periódica e a de depois de um comando):
  // uma resposta antiga não pode desfazer o que a mais nova já mostrou.
  if (seq < refreshApplied) return;
  refreshApplied = seq;

  const live = s.state === 'live';
  const busy = s.state === 'starting';
  $('startBtn').hidden = live;
  $('startBtn').disabled = busy;
  $('stopBtn').hidden = !live;
  for (const el of [...settingEls(), $('profilePreset')]) el.disabled = live || busy;
  // A janela/monitor pode ser trocada durante a transmissão, sem parar.
  $('sourceBtn').disabled = busy;
  $('sourceBtn').title = live ? 'Trocar a janela ou monitor (a transmissão continua)' : 'Escolher o que transmitir';
  $('settingsLocked').hidden = !(live || busy);
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
  // Dá nome à sala na lista de recentes: o nome que o dono escolheu e o nome dele.
  if (joined?.state === 'connected' && joinedWith && (joined.hostName || joined.roomName)) rememberRoom(joinedWith, joined.hostName, joined.roomName);
  const wanted = joined ? joined.roomUrl || 'about:blank' : s.roomUrl + (myName() ? `&name=${encodeURIComponent(myName())}` : '');
  if (wanted !== roomSrc) {
    roomSrc = wanted;
    $('room').src = wanted;
  }
  const inRoom = !!joined || s.allowShare;
  setButton($('startBtn'), 'play', busy ? 'Iniciando…' : inRoom ? 'Compartilhar minha tela' : 'Iniciar transmissão');
  setButton($('stopBtn'), 'stop', inRoom ? 'Parar de compartilhar' : 'Parar');
  renderView();
  if (!rotating) $('rotateBtn').disabled = !s.publicUrl;

  if (s.publicUrl !== shownUrl || !s.publicUrl) {
    shownUrl = s.publicUrl;
    renderLink(s);
  }
  renderCode(s);
  renderUpdate(s);
}

function renderLink(s) {
  const box = $('links');
  box.innerHTML = '';
  if (!s.publicUrl) {
    const status = document.createElement('div');
    status.className = 'muted';
    status.style.fontSize = '12.5px';
    status.textContent = s.tunnelStatus;
    box.append(status);
    return;
  }
  const row = document.createElement('div');
  row.className = 'copy-box';
  const code = document.createElement('code');
  code.textContent = code.title = s.publicUrl;
  const btn = document.createElement('button');
  btn.textContent = 'Copiar';
  btn.onclick = async () => {
    await navigator.clipboard.writeText(s.publicUrl);
    btn.textContent = 'Copiado';
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

let view = 'broadcast';   // 'broadcast' | 'rooms' | 'settings'
let roomDownSince = 0;     // desde quando a sala do amigo está sem conexão
let lobbyKey = '';        // o que os cards da lista de salas estão mostrando
let showList = false;     // na área de salas, ver a lista mesmo estando numa sala (ela continua aberta)
let lastStatus = null;
let joinedWith = '';      // código/link usado para entrar na sala atual (para a lista de recentes)

function myName() {
  return $('myName').value.trim().slice(0, 24);
}

// O nome fica guardado pelo app (arquivo de configurações), a cada letra digitada: o armazenamento
// do navegador embutido pode não chegar ao disco se o app for fechado logo depois.
let nameSaveTimer = null;
function saveName() {
  try { persistItem('gamestream-name', myName()); } catch { /* sem armazenamento */ }
  clearTimeout(nameSaveTimer);
  nameSaveTimer = setTimeout(() => fetch(api('app-settings'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: myName() }),
  }).catch(() => {}), 400);
}
$('myName').oninput = saveName;
$('myName').onchange = saveName;

async function loadName() {
  try {
    const s = await (await fetch(api('app-settings'))).json();
    if (s.name && !myName()) {
      $('myName').value = s.name;
      refresh();   // a sala embutida mostra o nome
    } else if (!s.name && myName()) {
      saveName();   // nome de versões antigas (só no navegador embutido): passa a ficar no app também
    } else if (!s.name && !myName() && s.defaultName) {
      // Ninguém preencheu: usa o nome do usuário do Windows (melhor que aparecer como "Anfitrião"). Dá para mudar.
      $('myName').value = String(s.defaultName).slice(0, 24);
      saveName();
      refresh();
    }
  } catch { /* app fechando */ }
}

/** Estou numa sala que eu mesmo criei? */
const inOwnRoom = () => !!lastStatus?.allowShare && !joined;

function renderView() {
  const own = inOwnRoom();
  if ((joined || own) && view === 'broadcast') view = 'rooms';   // quem está numa sala está na área de salas
  const settings = view === 'settings';
  const lobby = view === 'rooms' && (showList || (!joined && !own));

  $('navBroadcast').classList.toggle('active', view === 'broadcast');
  $('navRooms').classList.toggle('active', view === 'rooms');
  $('navSettings').classList.toggle('active', settings);
  $('settingsPage').hidden = !settings;
  $('lobby').hidden = !lobby;
  $('pageHeader').hidden = lobby;   // a lista de salas não tem título: o menu já diz onde você está
  $('workspace').hidden = lobby || settings;
  document.body.classList.toggle('no-side', lobby || settings);
  $('liveBadge').hidden = lobby;

  $('pageTitle').textContent = settings ? 'Configurações'
    : view === 'broadcast' ? 'Transmissão'
    : lobby ? 'Salas'
    : joined ? joined.roomName || `Sala de ${realName(joined.hostName) || 'um amigo'}`
    : (activeRoom()?.name || 'Minha sala');
  $('backToRoomsBtn').hidden = lobby || settings || !(joined || own);
  $('roomActionBtn').hidden = lobby || settings || !(joined || own);
  setButton($('roomActionBtn'), 'leave', joined ? 'Sair da sala' : 'Encerrar sala');

  // Painel lateral: código e link (transmissão ou sala própria) ou o aviso de convidado.
  $('hostSection').hidden = !!joined || lobby;
  $('guestSection').hidden = !joined;
  $('roomStats').hidden = !!joined || lobby;
  $('asideTitle').textContent = joined ? 'Sala' : 'Convite';
  $('viewerLabel').textContent = own ? 'Pessoas na sala' : 'Pessoas assistindo';
  $('guestStreamsRow').hidden = !own;
  $('myName').disabled = !!joined;   // o nome vai junto ao entrar; para mudar, saia e entre de novo

  // Sala do amigo fora do ar ou fechada: mostra um aviso no lugar da sala (senão aparece a página
  // de erro da Cloudflare dentro do quadro). Quando ela volta, recarrega a sala.
  // Versões diferentes entre você e quem criou a sala: muita coisa nova depende dos dois estarem atualizados.
  const mine = lastStatus?.version;
  const theirs = joined?.hostVersion;
  const differs = !!joined && joined.state === 'connected' && !!theirs && !!mine && theirs !== mine;
  $('joinMessage').hidden = !differs;
  if (differs) {
    $('joinMessage').className = 'warn';
    $('joinMessage').textContent = theirs === 'antiga'
      ? `Quem criou a sala está numa versão antiga do GameStream (você está na ${mine}). Se algo não funcionar, peça para atualizar.`
      : `Quem criou a sala está no GameStream ${theirs}, e você no ${mine}. Se algo não funcionar, atualizem para a mesma versão.`;
  }
  const down = !!joined && joined.state !== 'connected';
  if (down) {
    if (!roomDownSince) roomDownSince = Date.now();
    const gone = joined.reason === 'gone';
    const fatal = joined.state === 'closed';
    $('roomDownTitle').textContent = gone ? 'Esta sala não está no ar'
      : joined.reason === 'closed' ? 'A sala está fechada'
      : fatal ? 'Não foi possível entrar na sala'
      : 'Conectando à sala…';
    $('roomDownText').textContent = joined.message || '';
    $('roomDownNew').hidden = !(gone || fatal);
  } else {
    if (roomDownSince && Date.now() - roomDownSince > 4000 && joined) $('room').src = roomSrc;
    roomDownSince = 0;
  }
  $('roomDown').hidden = !down;
  $('room').style.visibility = down ? 'hidden' : '';
  if (lobby) {
    const key = JSON.stringify([myRooms(), recentRooms(), activeRoom()?.id, !!joined]);
    if (key !== lobbyKey) {
      lobbyKey = key;
      renderMyRooms();
      renderRecent();
    }
  } else {
    lobbyKey = '';
  }
}

$('navRooms').onclick = () => { view = 'rooms'; showList = false; renderView(); };
$('navSettings').onclick = () => { view = 'settings'; renderView(); loadAppSettings(); };
$('backToRoomsBtn').onclick = () => { showList = true; renderView(); };
$('navBroadcast').onclick = async () => {
  if (joined) {
    if (!(await ask('Sair da sala?', 'Você volta para a sua transmissão.', 'Sair'))) return;
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
  try { return JSON.parse(readItem('gamestream-my-rooms') || '[]'); } catch { return []; }
}

function storeMyRooms(list) {
  try { persistItem('gamestream-my-rooms', JSON.stringify(list.slice(0, 20))); } catch { /* sem armazenamento */ }
  syncRooms();
}

// Cada sala salva tem um GUID: o código dela ("nome-<guid>") é fixo, mesmo fechando e abrindo o app.
// O app avisa o servidor quais são, para publicar o endereço atual delas (ver RoomDirectory.cs).
function syncRooms() {
  fetch(api('rooms'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ rooms: myRooms().map((r) => r.guid).filter(Boolean) }),
  }).catch(() => {});
}

(function giveRoomsAGuid() {
  // Salas criadas antes do código fixo ganham o seu GUID (e perdem a chave antiga, que mudava a cada abertura).
  const list = myRooms();
  if (list.every((r) => r.guid)) return;
  storeMyRooms(list.map(({ key, pin, ...r }) => (r.guid ? r : { ...r, guid: crypto.randomUUID() })));
})();

let activeRoomId = null;   // sala salva que está aberta agora (enquanto o servidor estiver em modo sala)

/** A sala salva que está aberta agora, se houver. */
function activeRoom() {
  return inOwnRoom() ? myRooms().find((r) => r.id === activeRoomId) || null : null;
}

// Cada sala salva tem a sua chave (o "?k=" do link e os 6 números do código). Ao reabrir a sala, a chave
// volta a valer: quem estava nela entra de novo sozinho. A transmissão também guarda a dela.
// As palavras do começo do código vêm do endereço do túnel, que muda quando o app é fechado e aberto.
let broadcastKey = null;   // { key, pin } da transmissão, para voltar a ela ao encerrar uma sala

const keyQuery = (k) => (k ? `&key=${k.key}&pin=${k.pin}` : '');

async function openRoom(room) {
  const others = lastStatus?.viewers > 0;
  if (others && !(await ask(`Abrir "${room.name}"?`, 'Quem está conectado agora sai e volta sozinho quando você voltar para onde estava.', 'Abrir'))) return;
  if (!room.guid) {
    room = { ...room, guid: crypto.randomUUID() };
    storeMyRooms(myRooms().map((r) => (r.id === room.id ? room : r)));
  }
  if (lastStatus && !lastStatus.allowShare && !joined) broadcastKey = { key: lastStatus.viewKey, pin: lastStatus.pin };
  await fetch(`${api('allow-share')}&on=1&name=${encodeURIComponent(room.name)}&room=${room.guid}`, { method: 'POST' });
  activeRoomId = room.id;
  try { sessionStorage.setItem('gamestream-active-room', room.id); } catch { /* sem armazenamento */ }
  storeMyRooms(myRooms().map((r) => (r.id === room.id ? { ...r, at: Date.now() } : r)));
  showList = false;
  await refresh();
}

async function createRoom() {
  const name = $('newRoomName').value.trim().slice(0, 32) || `Sala ${myRooms().length + 1}`;
  $('newRoomDialog').close();
  if (joined) {
    // Na sala de um amigo: sai dela antes (senão a sala nova abre "por trás" e a tela continua na do amigo).
    if (!(await ask('Sair da sala do seu amigo?', `Você sai dela para abrir "${name}".`, 'Sair e criar'))) return;
    await leaveRoom();
  }
  const room = { id: Math.random().toString(36).slice(2, 10), name, guid: crypto.randomUUID(), at: Date.now() };
  storeMyRooms([room, ...myRooms()]);
  $('newRoomName').value = '';
  $('createRoomBtn').disabled = true;
  try { await openRoom(room); } finally { $('createRoomBtn').disabled = false; }
  renderView();
}

/** Fecha a sala aberta (ela continua salva em "Minhas salas"). */
async function endOwnRoom() {
  const name = activeRoom()?.name || 'a sala';
  if (!(await ask(`Encerrar "${name}"?`, 'Quem está na sala fica esperando e entra de novo sozinho quando você reabrir. O código continua o mesmo enquanto o app estiver aberto.', 'Encerrar', true))) return false;
  if (broadcastKey) {
    await fetch(`${api('allow-share')}&on=0${keyQuery(broadcastKey)}`, { method: 'POST' });
  } else {
    await fetch(`${api('allow-share')}&on=0`, { method: 'POST' });
    await fetch(api('rotate-key'), { method: 'POST' });
  }
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
  const list = myRooms();
  $('myRoomsCount').textContent = list.length;
  for (const r of list) {
    const isOpen = open?.id === r.id;
    box.append(roomCard({
      name: r.name,
      detail: isOpen ? 'Aberta agora' : `de ${myName() || 'você'}`,
      detailClass: isOpen ? 'open-tag' : '',
      open: isOpen,
      primary: isOpen,
      action: isOpen ? 'Voltar para a sala' : 'Abrir',
      onAction: isOpen ? () => { showList = false; renderView(); } : async () => {
        if (joined) {
          if (!(await ask('Sair da sala do seu amigo?', `Você sai dela para abrir "${r.name}".`, 'Sair e abrir'))) return;
          await leaveRoom();
        }
        await openRoom(r);
      },
      removeTitle: isOpen ? 'Encerre a sala antes de apagá-la' : 'Apagar esta sala',
      removeDisabled: isOpen,
      onRemove: async () => {
        if (!(await ask(`Apagar "${r.name}"?`, 'Ela sai da sua lista de salas.', 'Apagar', true))) return;
        storeMyRooms(myRooms().filter((x) => x.id !== r.id));
        renderMyRooms();
      },
    }));
  }
  const add = document.createElement('button');
  add.className = 'room-card new';
  add.append(icon('plus'), 'Nova sala');
  add.onclick = openNewRoom;
  box.append(add);
}

function openNewRoom() {
  $('newRoomName').value = '';
  $('newRoomDialog').showModal();
  $('newRoomName').focus();
}

$('createRoomBtn').onclick = createRoom;
$('newRoomCancel').onclick = () => $('newRoomDialog').close();
$('newRoomName').onkeydown = (e) => {
  if (e.key !== 'Enter') return;
  // Sem isto, o mesmo Enter "clica" no card "Nova sala" (que recebe o foco quando a janela fecha) e ela reabre.
  e.preventDefault();
  createRoom();
};
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
    showJoinError('Escreva o seu nome no menu à esquerda antes de entrar.');
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

// O som da sala é tocado pelo próprio app (RoomAudioPlayer.cs); a página da sala avisa qual tela toca.
window.addEventListener('message', (e) => {
  if (e.source !== $('room').contentWindow) return;
  const m = e.data;
  if (!m || m.gs !== 'audio') return;
  const stream = Number.isInteger(m.stream) && m.stream >= 0 && m.stream <= 65535 ? m.stream : null;
  const volume = Math.min(4, Math.max(0, Number(m.volume) || 0));
  fetch(api('listen'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ stream, volume, muted: m.muted === true }),
  }).catch(() => {});
});
$('roomDownLeave').onclick = () => leaveRoom();
$('roomDownNew').onclick = async () => {
  await leaveRoom();
  view = 'rooms';
  showList = true;
  renderView();
  $('joinUrl').focus();
};
$('joinUrl').onkeydown = (e) => { if (e.key === 'Enter') joinRoom(); };

// ---------- Salas recentes ----------
// Só ajudam enquanto a sala continuar aberta: o código muda quando quem a criou fecha o app.

function recentRooms() {
  try { return JSON.parse(readItem('gamestream-rooms') || '[]'); } catch { return []; }
}

function storeRooms(list) {
  try { persistItem('gamestream-rooms', JSON.stringify(list.slice(0, 8))); } catch { /* sem armazenamento */ }
}

/** Código fixo "tavena-do-wilfer-<guid>" → "Tavena do wilfer" (o nome da sala vem no começo do código). */
function titleFromCode(code) {
  const m = /^([a-z0-9-]+)-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.exec(code || '');
  if (!m) return '';
  const text = m[1].replace(/-/g, ' ');
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** Nomes padrão (de quem não preencheu "Seu nome", ou da 0.5.7) não servem para identificar a sala. */
const realName = (name) => (name && name !== 'Anfitrião' && name !== 'Convidado' ? name : '');

function rememberRoom(code, label, title) {
  const old = recentRooms().find((r) => r.code === code);
  const next = { code, label: realName(label) || old?.label || '', title: title || old?.title || '', at: Date.now() };
  if (old && old.label === next.label && old.title === next.title && Date.now() - old.at < 60_000) return;   // nada mudou
  // A mesma sala com outro código (o dono gerou um novo): fica só a mais recente.
  const sameRoom = (r) => next.title && next.label && r.title === next.title && r.label === next.label;
  storeRooms([next, ...recentRooms().filter((r) => r.code !== code && !sameRoom(r))]);
}

function renderRecent() {
  const list = recentRooms();
  $('recentCard').hidden = !list.length;
  const box = $('recentRooms');
  box.textContent = '';
  for (const r of list) {
    box.append(roomCard({
      // Em cima, o nome que o dono deu à sala; embaixo, quem criou e quando você entrou.
      name: r.title || titleFromCode(r.code) || (realName(r.label) ? `Sala de ${r.label}` : 'Sala'),
      detail: realName(r.label) ? `de ${r.label}` : 'Criador ainda não informado',
      action: 'Entrar',
      onAction: () => joinRoom(r.code),
      removeTitle: 'Tirar da lista',
      onRemove: () => { storeRooms(recentRooms().filter((x) => x.code !== r.code)); renderRecent(); },
    }));
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
  $('codeCopy').textContent = 'Copiado';
  setTimeout(() => ($('codeCopy').textContent = 'Copiar'), 1500);
};

// ---------- Menu e painel lateral recolhíveis ----------

function setNavCollapsed(collapsed, save = true) {
  document.body.classList.toggle('nav-collapsed', collapsed);
  $('navToggle').replaceChildren(icon(collapsed ? 'panel-left-open' : 'panel-left-close'));
  $('navToggle').title = collapsed ? 'Expandir o menu' : 'Recolher o menu';
  if (save) { try { persistItem('gamestream-nav-collapsed', collapsed ? '1' : '0'); } catch { /* sem armazenamento */ } }
}
$('navToggle').onclick = () => setNavCollapsed(!document.body.classList.contains('nav-collapsed'));
try { setNavCollapsed(readItem('gamestream-nav-collapsed') === '1', false); } catch { setNavCollapsed(false, false); }

function setSideCollapsed(collapsed, save = true) {
  document.body.classList.toggle('side-collapsed', collapsed);
  $('sideShow').hidden = !collapsed;
  if (save) { try { persistItem('gamestream-side-collapsed', collapsed ? '1' : '0'); } catch { /* sem armazenamento */ } }
}
$('sideHide').onclick = () => setSideCollapsed(true);
$('sideShow').onclick = () => setSideCollapsed(false);
try { setSideCollapsed(readItem('gamestream-side-collapsed') === '1', false); } catch { /* sem armazenamento */ }

// ---------- Controle do link ----------

let rotating = false;
$('rotateBtn').onclick = async () => {
  if (!(await ask('Gerar um código novo?', 'Todos são desconectados e precisam do código ou link novo para voltar.', 'Gerar'))) return;
  rotating = true;
  $('rotateBtn').disabled = true;
  const room = activeRoom();
  if (room) {
    // Sala salva: um GUID novo (o código antigo para de valer de vez).
    const fresh = { ...room, guid: crypto.randomUUID() };
    storeMyRooms(myRooms().map((r) => (r.id === room.id ? fresh : r)));
    await fetch(`${api('allow-share')}&on=1&name=${encodeURIComponent(fresh.name)}&room=${fresh.guid}&fresh=1`, { method: 'POST' });
  } else {
    await fetch(api('rotate-key'), { method: 'POST' });
    const s = await (await fetch(api('status'))).json().catch(() => null);
    if (s && !s.allowShare) broadcastKey = { key: s.viewKey, pin: s.pin };
  }
  rotating = false;
  refresh();
};
$('maxViewers').onchange = () => {
  try { persistItem('gamestream-max-viewers', $('maxViewers').value); } catch { /* sem armazenamento */ }
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
  $('myName').value = readItem('gamestream-name') || '';
  activeRoomId = sessionStorage.getItem('gamestream-active-room');
} catch { /* sem armazenamento */ }
loadSources(saved);
loadEncoders();
loadName();
syncRooms();
// O limite de pessoas fica no servidor; reaplica o que foi escolhido da última vez.
try {
  const max = readItem('gamestream-max-viewers');
  if (max !== null) fetch(`${api('max-viewers')}&n=${max}`, { method: 'POST' });
} catch { /* sem armazenamento */ }
refresh();
setInterval(refresh, 1000);
setInterval(updateMeter, 150);
