// Webcam emotion read. The user's own camera is analysed on-device; only a tiny summary (a mood label and
// confidence) is sent to Ilham. Raw video never leaves the machine.
//
// Face expression recognition uses face-api.js if its models are available (loaded once from a CDN, or from
// /models if you vendor them for offline use). If they can't load, the camera still shows "she can see you"
// presence, and you can tell her how you feel in words — she degrades gracefully, never breaks.
'use strict';

const FACEAPI_SRC = 'https://cdn.jsdelivr.net/npm/@vladmandic/face-api@1.7.15/dist/face-api.esm.js';
const MODEL_URLS = ['/models', 'https://cdn.jsdelivr.net/npm/@vladmandic/face-api@1.7.15/model'];

export class Perception {
  constructor(video, { onRead } = {}) {
    this.video = video; this.onRead = onRead; this.running = false; this.faceapi = null; this.ready = false;
  }

  async start() {
    this.stream = await navigator.mediaDevices.getUserMedia({ video: { width: 320, height: 240, facingMode: 'user' } });
    this.video.srcObject = this.stream; this.video.hidden = false;
    await this.video.play().catch(() => {});
    this.running = true;
    this._loadModels(); // in the background; presence works meanwhile
    this._loop();
    return true;
  }

  async _loadModels() {
    try {
      this.faceapi = await import(/* @vite-ignore */ FACEAPI_SRC);
      for (const base of MODEL_URLS) {
        try {
          await this.faceapi.nets.tinyFaceDetector.loadFromUri(base);
          await this.faceapi.nets.faceExpressionNet.loadFromUri(base);
          this.ready = true; break;
        } catch { /* try next source */ }
      }
    } catch { this.ready = false; } // offline / blocked: presence-only
  }

  async _loop() {
    while (this.running) {
      let report = { FaceExpression: null, FaceConfidence: 0, EyesTired: false, VoiceEnergy: null, VoicePace: null, Transcript: null };
      if (this.ready && this.faceapi) {
        try {
          const det = await this.faceapi
            .detectSingleFace(this.video, new this.faceapi.TinyFaceDetectorOptions({ inputSize: 224 }))
            .withFaceExpressions();
          if (det?.expressions) {
            const map = { happy: 'happy', sad: 'sad', angry: 'angry', fearful: 'worried', disgusted: 'annoyed', surprised: 'surprised', neutral: 'neutral' };
            const [name, score] = Object.entries(det.expressions).sort((a, b) => b[1] - a[1])[0];
            report.FaceExpression = map[name] || 'neutral';
            report.FaceConfidence = score;
          }
        } catch { /* a frame failed; skip it */ }
      }
      this.onRead?.(report, this.ready);
      await new Promise(r => setTimeout(r, this.ready ? 1500 : 3000));
    }
  }

  stop() {
    this.running = false;
    this.stream?.getTracks().forEach(t => t.stop());
    this.video.hidden = true; this.video.srcObject = null;
  }
}
