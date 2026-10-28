'use strict';

// Captura do som para navegadores sem MediaStreamTrackProcessor (ex.: Firefox): junta as amostras em blocos de
// 20 ms (960 amostras a 48 kHz, estéreo) e manda para a página, que repassa ao worker de codificação.

const BLOCK = 960;

class CaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.block = new Float32Array(BLOCK * 2);   // planar: canal esquerdo, depois o direito
    this.filled = 0;
    this.sent = 0;                              // amostras já enviadas (para o timestamp)
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || !input.length) return true;
    const left = input[0];
    const right = input[1] || input[0];         // mono vira estéreo
    for (let i = 0; i < left.length; i++) {
      this.block[this.filled] = left[i];
      this.block[BLOCK + this.filled] = right[i];
      if (++this.filled === BLOCK) {
        const data = this.block;
        this.port.postMessage({ data, timestamp: Math.round((this.sent / sampleRate) * 1e6) }, [data.buffer]);
        this.sent += BLOCK;
        this.block = new Float32Array(BLOCK * 2);
        this.filled = 0;
      }
    }
    return true;
  }
}

registerProcessor('gs-capture', CaptureProcessor);
