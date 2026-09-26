'use strict';

// Interface do app. A captura e a codificação rodam no C# (FFmpeg + WASAPI);
// esta página só envia comandos e mostra o status.

const HOST_TOKEN = new URLSearchParams(location.search).get('t');
const $ = (id) => document.getElementById(id);
const api = (path) => `/api/${path}?t=${HOST_TOKEN}`;

const DEFAULTS = {
  height: 1080, fps: 60, constantFps: true, cursor: true,
  encoder: 'auto', rateControl: 'cbr', bitrateKbps: 8000, preset: 'balanced',
  keyframeSeconds: 1, profile: 'high', audioMode: 'window', audioKbps: 160, audioGainDb: 6,
};

const PRESETS = {
  quality:  { height: 1080, fps: 60, constantFps: true, encoder: 'auto', rateControl: 'cbr', bitrateKbps: 10000, preset: 'quality' },
  balanced: { height: 900,  fps: 60, constantFps: true, encoder: 'auto', rateControl: 'cbr', bitrateKbps: 8000,  preset: 'balanced' },
  stable30: { height: 720,  fps: 30, constantFps: true, encoder: 'auto', rateControl: 'cbr', bitrateKbps: 5000,  preset: 'balanced' },
  cpu:      { height: 720,  fps: 30, constantFps: true, encoder: 'x264', rateControl: 'cbr', bitrateKbps: 4500,  preset: 'speed' },
  light:    { height: 540,  fps: 30, constantFps: true, encoder: 'auto', rateControl: 'cbr', bitrateKbps: 2500,  preset: 'balanced' },
};

const settingEls = () => [...document.querySelectorAll('[data-setting]')];

let shownUrl;
let previewShown = false;
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
      profilePreset: $('profilePreset').value,
      // A janela muda de identificador a cada execução; guardamos nome e programa para reencontrá-la.
      sourceName: src?.name, sourceDetail: src?.detail,
    }));
  } catch { /* armazenamento indisponível */ }
}

function loadPrefs() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem('gamestream-settings') || '{}'); } catch { /* nada salvo */ }
  applySettings({ ...DEFAULTS, ...saved });
  $('profilePreset').value = saved.profilePreset || '';
  return saved;
}

$('profilePreset').onchange = () => {
  const p = PRESETS[$('profilePreset').value];
  if (p) applySettings(p);
  savePrefs();
};
for (const el of settingEls()) {
  el.addEventListener('change', () => {
    $('profilePreset').value = '';   // mexeu em algo: passa a ser "Personalizado"
    savePrefs();
  });
}

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
  try { db = (await (await fetch(api('level'))).json()).db; } catch { /* app fechando */ }
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
}

// ---------- Início / parada ----------

async function start() {
  if (!$('source').value) {
    showMessage('Escolha uma janela ou monitor.', true);
    return;
  }
  savePrefs();
  $('startBtn').disabled = true;
  await fetch(api('start'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ source: $('source').value, ...readSettings() }),
  });
  refresh();
}

async function stop() {
  await fetch(api('stop'), { method: 'POST' });
  refresh();
}

// ---------- Status ----------

function showMessage(text, isError = false) {
  $('message').textContent = text || '';
  $('message').className = isError ? 'error' : 'muted';
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
  showMessage(s.message, s.state === 'error');

  $('viewerCount').textContent = `${s.viewers} / ${s.maxViewers}`;
  if (document.activeElement !== $('maxViewers')) $('maxViewers').value = String(s.maxViewers);
  $('sourceOut').textContent = live ? s.source : '—';
  $('sourceOut').title = s.source || '';
  $('sizeOut').textContent = live ? s.outputSize : '—';
  $('fpsOut').textContent = live ? `${s.fps} fps` : '—';
  $('kbpsOut').textContent = live ? `${(s.kbps / 1000).toFixed(1)} Mbps` : '—';
  $('encoderOut').textContent = live ? s.encoder : '—';
  $('audioOut').textContent = live ? s.audio : '—';

  // Prévia: a própria página do espectador, em modo silencioso.
  if (!previewShown) {
    $('preview').src = s.previewUrl;
    previewShown = true;
  }
  if (!rotating) $('rotateBtn').disabled = !s.publicUrl;
  $('preview').hidden = !live;
  $('empty').hidden = live;

  if (s.publicUrl !== shownUrl || !s.publicUrl) {
    shownUrl = s.publicUrl;
    renderLink(s);
  }
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

// ---------- Controle do link ----------

let rotating = false;
$('rotateBtn').onclick = async () => {
  if (!confirm('Gerar um link novo? Todos que estão assistindo serão desconectados e precisarão do endereço novo.')) return;
  rotating = true;
  $('rotateBtn').disabled = true;
  await fetch(api('rotate-key'), { method: 'POST' });
  rotating = false;
  refresh();
};
$('maxViewers').onchange = () => fetch(`${api('max-viewers')}&n=${$('maxViewers').value}`, { method: 'POST' });

// ---------- Inicialização ----------

$('startBtn').onclick = start;
$('stopBtn').onclick = stop;
const saved = loadPrefs();
loadSources(saved);
refresh();
setInterval(refresh, 1000);
setInterval(updateMeter, 150);
