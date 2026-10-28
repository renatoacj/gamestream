'use strict';

// Worker de quem compartilha a tela pelo navegador: recebe os quadros capturados,
// codifica (H.264/VP8 + Opus) e devolve pacotes prontos para a página enviar à sala.
// Roda fora da página para continuar funcionando com a aba em segundo plano.

const HEADER = 12;                      // [tipo][flags][transmissão u16][timestamp f64]
const KIND_VIDEO = 1, KIND_AUDIO = 2, KIND_THUMB = 3;
const KEYFRAME_INTERVAL_US = 2_000_000; // quadro-chave a cada 2 s (ou quando a sala pedir)
const THUMB_INTERVAL_MS = 8000;

let running = false;
let videoEncoder = null;
let audioEncoder = null;
let encoderConfig = null;
let configuredSize = '';
let forceKeyframe = true;
let lastKeyframeTs = -Infinity;
let lastThumb = 0;
let audioOk = true;
let ticker = null;

self.onmessage = (e) => {
  const m = e.data;
  if (m.type === 'start') {
    running = true;
    encoderConfig = m.encoderConfig;
    if (m.video) {
      pump(m.video, onVideoFrame).catch(fail);
      if (m.audio) pump(m.audio, onAudioData).catch((err) => console.warn('áudio', err));
    } else {
      // Sem MediaStreamTrackProcessor (ex.: Firefox): a página manda cada quadro quando este relógio pede.
      // O relógio fica aqui porque, com a aba em segundo plano, o worker não é freado como a página.
      ticker = setInterval(() => { if (running) self.postMessage({ type: 'tick' }); }, 1000 / m.fps);
    }
  } else if (m.type === 'frame') {
    try {
      if (running) onVideoFrame(m.frame);
    } catch (err) {
      fail(err);
    } finally {
      m.frame.close();
    }
  } else if (m.type === 'pcm') {
    // Som vindo do AudioWorklet (48 kHz, estéreo, planar) quando não há MediaStreamTrackProcessor.
    if (!running || typeof AudioData === 'undefined') return;
    const data = new AudioData({
      format: 'f32-planar', sampleRate: 48000, numberOfChannels: 2,
      numberOfFrames: m.data.length / 2, timestamp: m.timestamp, data: m.data,
    });
    try { onAudioData(data); } finally { data.close(); }
  } else if (m.type === 'keyframe') {
    forceKeyframe = true;
    if (m.thumbnail) lastThumb = 0;   // a sala acabou de aceitar a transmissão: manda a miniatura já
  } else if (m.type === 'stop') {
    running = false;
    clearInterval(ticker);
    for (const enc of [videoEncoder, audioEncoder]) {
      if (enc && enc.state !== 'closed') enc.close();
    }
  }
};

function fail(err) {
  if (running) self.postMessage({ type: 'error', message: String(err?.message || err) });
  running = false;
}

async function pump(readable, handler) {
  const reader = readable.getReader();
  while (running) {
    const { value, done } = await reader.read();
    if (done) break;
    try {
      if (running) await handler(value);
    } finally {
      value.close();
    }
  }
  reader.cancel().catch(() => {});
}

function post(kind, key, timestamp, chunkOrBytes) {
  const size = chunkOrBytes.byteLength;
  const buf = new ArrayBuffer(HEADER + size);
  const view = new DataView(buf);
  view.setUint8(0, kind);
  view.setUint8(1, key ? 1 : 0);
  view.setFloat64(4, timestamp, true);
  const body = new Uint8Array(buf, HEADER);
  if (chunkOrBytes.copyTo) chunkOrBytes.copyTo(body);
  else body.set(chunkOrBytes);
  self.postMessage({ type: 'packet', buf }, [buf]);
}

// ---------- Vídeo ----------

function onVideoFrame(frame) {
  // Dimensões pares (exigência do H.264); muda se a janela capturada for redimensionada.
  const width = frame.displayWidth & ~1;
  const height = frame.displayHeight & ~1;
  const size = `${width}x${height}`;
  if (!videoEncoder || videoEncoder.state !== 'configured' || size !== configuredSize) {
    if (!videoEncoder || videoEncoder.state === 'closed') {
      videoEncoder = new VideoEncoder({
        output: (chunk) => post(KIND_VIDEO, chunk.type === 'key', chunk.timestamp, chunk),
        error: fail,
      });
    }
    videoEncoder.configure({ ...encoderConfig, width, height });
    configuredSize = size;
    forceKeyframe = true;
  }

  maybeThumbnail(frame);

  // Sem fila: se o codificador atrasar, pula o quadro em vez de acumular atraso.
  if (videoEncoder.encodeQueueSize > 2) return;

  const keyFrame = forceKeyframe || frame.timestamp - lastKeyframeTs >= KEYFRAME_INTERVAL_US;
  if (keyFrame) {
    forceKeyframe = false;
    lastKeyframeTs = frame.timestamp;
  }
  videoEncoder.encode(frame, { keyFrame });
}

// Miniatura para a lista de telas da sala.
function maybeThumbnail(frame) {
  const now = performance.now();
  if (now - lastThumb < THUMB_INTERVAL_MS && lastThumb !== 0) return;
  lastThumb = now;

  const w = 320;
  const h = Math.max(2, Math.round((w * frame.displayHeight) / frame.displayWidth));
  const canvas = new OffscreenCanvas(w, h);
  canvas.getContext('2d').drawImage(frame, 0, 0, w, h);
  canvas.convertToBlob({ type: 'image/jpeg', quality: 0.6 })
    .then((blob) => blob.arrayBuffer())
    .then((bytes) => { if (running) post(KIND_THUMB, false, 0, new Uint8Array(bytes)); })
    .catch(() => {});
}

// ---------- Áudio ----------

function onAudioData(data) {
  if (!audioOk) return;
  // A sala foi avisada de áudio Opus 48 kHz estéreo; outros formatos ficam de fora.
  if (data.sampleRate !== 48000 || data.numberOfChannels !== 2) {
    audioOk = false;
    return;
  }
  if (!audioEncoder) {
    audioEncoder = new AudioEncoder({
      output: (chunk) => post(KIND_AUDIO, true, chunk.timestamp, chunk),
      error: (e) => { console.warn('AudioEncoder', e); audioOk = false; },
    });
    audioEncoder.configure({ codec: 'opus', sampleRate: 48000, numberOfChannels: 2, bitrate: 128_000 });
  }
  if (audioEncoder.state === 'configured') audioEncoder.encode(data);
}
