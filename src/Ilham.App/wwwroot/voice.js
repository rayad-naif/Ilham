// Voice: speech-in (Web Speech recognition, Urdu + English) and speech-out (speech synthesis) with a live
// amplitude signal for lip-sync. All on-device in the browser; nothing is uploaded.
'use strict';

/** Speech recognition. Calls onResult(text, isFinal) as you speak. */
export class Listener {
  constructor({ lang = 'ur-PK', onResult, onState }) {
    const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
    this.supported = !!SR;
    this.onResult = onResult; this.onState = onState; this.lang = lang;
    this.active = false;
    if (!SR) return;
    this.rec = new SR();
    this.rec.lang = lang; this.rec.interimResults = true; this.rec.continuous = false;
    this.rec.onresult = e => {
      let txt = '', final = false;
      for (let i = e.resultIndex; i < e.results.length; i++) { txt += e.results[i][0].transcript; if (e.results[i].isFinal) final = true; }
      this.onResult?.(txt.trim(), final);
    };
    this.rec.onend = () => { this.active = false; this.onState?.(false); };
    this.rec.onerror = () => { this.active = false; this.onState?.(false); };
  }
  setLang(lang) { this.lang = lang; if (this.rec) this.rec.lang = lang; }
  start() { if (!this.rec || this.active) return; try { this.rec.start(); this.active = true; this.onState?.(true); } catch { /* already running */ } }
  stop() { if (this.rec && this.active) this.rec.stop(); }
}

/** Speech synthesis with lip-sync amplitude. Picks a female voice matching the language when available. */
export class Voice {
  constructor({ onAmplitude } = {}) {
    this.enabled = true;
    this.onAmplitude = onAmplitude;
    this.synth = window.speechSynthesis;
    this.supported = !!this.synth;
    this.voices = [];
    if (this.supported) {
      const load = () => { this.voices = this.synth.getVoices(); };
      load(); this.synth.onvoiceschanged = load;
    }
  }

  _pickVoice(lang) {
    const v = this.voices;
    const femaleHint = /female|woman|zira|ayesha|salma|naayf|google|aria|samantha|neerja|heera/i;
    const byLang = v.filter(x => x.lang?.toLowerCase().startsWith(lang.slice(0, 2)));
    return byLang.find(x => femaleHint.test(x.name)) || byLang[0] || v.find(x => femaleHint.test(x.name)) || v[0] || null;
  }

  /** Speaks text; resolves when finished. Drives onAmplitude via a synthetic envelope (boundary events). */
  speak(text, lang = 'ur-PK', mood = 'neutral') {
    if (!this.supported || !this.enabled || !text.trim()) return Promise.resolve();
    this.synth.cancel();
    return new Promise(resolve => {
      const u = new SpeechSynthesisUtterance(text);
      u.voice = this._pickVoice(lang); u.lang = u.voice?.lang || lang;
      // Tone by mood.
      const t = { excited: [1.12, 1.15], happy: [1.06, 1.08], loving: [1.0, 0.95], teasing: [1.05, 1.1],
        worried: [0.97, 0.95], annoyed: [1.03, 1.0], angry: [1.08, 1.05], sad: [0.9, 0.9] }[mood] || [1, 1];
      u.rate = t[0]; u.pitch = t[1];
      let raf;
      const animate = () => { // gentle mouth motion while speaking (boundary events are sparse)
        this.onAmplitude?.(0.25 + Math.abs(Math.sin(performance.now() / 90)) * 0.5);
        raf = requestAnimationFrame(animate);
      };
      u.onstart = () => animate();
      u.onboundary = () => { this.onAmplitude?.(0.8); };
      const done = () => { cancelAnimationFrame(raf); this.onAmplitude?.(0); resolve(); };
      u.onend = done; u.onerror = done;
      this.synth.speak(u);
    });
  }
  cancel() { if (this.supported) this.synth.cancel(); this.onAmplitude?.(0); }
}

/** Reads microphone loudness and speaking pace to estimate "tone" for Ilham. */
export class ToneMeter {
  async start() {
    try {
      this.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      this.ac = new AudioContext();
      const src = this.ac.createMediaStreamSource(this.stream);
      this.an = this.ac.createAnalyser(); this.an.fftSize = 512;
      src.connect(this.an); this.buf = new Uint8Array(this.an.frequencyBinCount);
      return true;
    } catch { return false; }
  }
  /** Current loudness 0..1. */
  energy() {
    if (!this.an) return 0;
    this.an.getByteTimeDomainData(this.buf);
    let sum = 0; for (const v of this.buf) sum += (v - 128) ** 2;
    return Math.min(1, Math.sqrt(sum / this.buf.length) / 40);
  }
  stop() { this.stream?.getTracks().forEach(t => t.stop()); this.ac?.close(); this.an = null; }
}
