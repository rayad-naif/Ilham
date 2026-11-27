// Ilham's face — a stylised, expressive avatar drawn on a 2D canvas.
// It needs no model file and runs fully offline. Mood sets target expression values; the render loop
// eases toward them, blinks, breathes, and opens the mouth to a live "amplitude" for lip-sync.
//
// Swapping in a photoreal 3D avatar later: build a .vrm of Ilham in VRoid Studio from your reference
// image, then a three-vrm renderer can replace this one behind the same setMood()/setAmplitude() API.
'use strict';

// Target expression per mood: brow height/angle, eye openness, mouth curve/open, blush, head tilt, colour.
const EXPRESSIONS = {
  neutral:  { brow: 0.0, browAngle: 0.0, eye: 1.0, smile: 0.1, blush: 0.1, tilt: 0.0, hue: 318 },
  happy:    { brow: 0.1, browAngle: 0.0, eye: 0.85, smile: 0.9, blush: 0.4, tilt: -0.03, hue: 330 },
  loving:   { brow: 0.15, browAngle: 0.1, eye: 0.7, smile: 0.7, blush: 0.75, tilt: 0.06, hue: 345 },
  teasing:  { brow: 0.2, browAngle: -0.25, eye: 0.8, smile: 0.6, blush: 0.3, tilt: 0.1, hue: 300 },
  excited:  { brow: 0.35, browAngle: 0.05, eye: 1.15, smile: 1.0, blush: 0.55, tilt: -0.05, hue: 335 },
  worried:  { brow: 0.1, browAngle: 0.5, eye: 1.05, smile: -0.3, blush: 0.15, tilt: 0.04, hue: 310 },
  annoyed:  { brow: -0.3, browAngle: -0.5, eye: 0.75, smile: -0.4, blush: 0.1, tilt: 0.0, hue: 295 },
  angry:    { brow: -0.5, browAngle: -0.7, eye: 0.9, smile: -0.6, blush: 0.2, tilt: 0.0, hue: 355 },
  sad:      { brow: 0.05, browAngle: 0.7, eye: 0.7, smile: -0.7, blush: 0.05, tilt: 0.12, hue: 265 },
};

const lerp = (a, b, t) => a + (b - a) * t;

export class Avatar {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.cur = { ...EXPRESSIONS.neutral };
    this.target = { ...EXPRESSIONS.neutral };
    this.amp = 0;          // live mouth openness from speech (0..1)
    this.ampSmooth = 0;
    this.blink = 1;        // 1 = open, 0 = shut
    this.nextBlink = 0;
    this.gaze = { x: 0, y: 0 };
    this.gazeTarget = { x: 0, y: 0 };
    this.t = 0;
    this._resize();
    addEventListener('resize', () => this._resize());
    canvas.addEventListener('pointermove', e => {
      const r = canvas.getBoundingClientRect();
      this.gazeTarget.x = ((e.clientX - r.left) / r.width - 0.5) * 2;
      this.gazeTarget.y = ((e.clientY - r.top) / r.height - 0.5) * 2;
    });
    requestAnimationFrame(this._loop.bind(this));
  }

  /** Switch expression to a mood name. */
  setMood(mood) { this.target = { ...(EXPRESSIONS[mood] || EXPRESSIONS.neutral) }; this.mood = mood; }

  /** Feed live speech loudness (0..1) so the mouth moves while she talks. */
  setAmplitude(a) { this.amp = Math.max(0, Math.min(1, a)); }

  /** Makes her slowly drift around the given container, like a presence that moves. */
  startWander(container) {
    this.wrap = container;
    if (!container) return;
    container.style.position ||= 'relative';
    const pick = () => {
      // A new resting spot somewhere in the pane; demanding/angry moods roam wider and faster.
      const roam = (this.mood === 'angry' || this.mood === 'annoyed') ? 0.34 : (this.mood === 'sad' ? 0.08 : 0.2);
      this.wanderTo = { x: (Math.random() - 0.5) * 2 * roam, y: (Math.random() - 0.5) * 2 * roam * 0.7 };
      this.wanderAt = 0; this.wanderDur = 4 + Math.random() * 5;
    };
    pick();
    this.wanderPos ??= { x: 0, y: 0 };
    const tick = (now) => {
      const dt = Math.min(0.05, (now - (this._wlast || now)) / 1000); this._wlast = now;
      this.wanderAt += dt;
      if (this.wanderAt > this.wanderDur) pick();
      const k = 1 - Math.pow(0.2, dt); // ease toward target spot
      this.wanderPos.x = lerp(this.wanderPos.x, this.wanderTo.x, k * 0.5);
      this.wanderPos.y = lerp(this.wanderPos.y, this.wanderTo.y, k * 0.5);
      const bob = Math.sin(now / 700) * 0.01 + (this.pulseT > 0 ? Math.sin(this.pulseT * 30) * this.pulseT * 0.12 : 0);
      if (this.pulseT > 0) this.pulseT = Math.max(0, this.pulseT - dt * 2.2);
      this.canvas.style.transform =
        `translate(${this.wanderPos.x * 100}%, ${(this.wanderPos.y + bob) * 100}%) scale(${1 + (this.pulseT || 0) * 0.08})`;
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }

  /** A little bounce when she does something emphatic (an initiative, a demand). */
  pulse(kind) {
    this.pulseT = kind === 'demand' || kind === 'impatient' ? 1 : 0.6;
    if (this.wanderTo) this.wanderAt = this.wanderDur; // re-pick a spot, so a demand makes her move
  }

  _resize() {
    const dpr = Math.min(devicePixelRatio || 1, 2);
    const size = Math.min(this.canvas.clientWidth, this.canvas.clientHeight) || 420;
    this.canvas.width = size * dpr; this.canvas.height = size * dpr;
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    this.S = size;
  }

  _loop(now) {
    const dt = Math.min(0.05, (now - (this._last || now)) / 1000); this._last = now; this.t += dt;

    // Ease current expression toward target.
    for (const k in this.target) this.cur[k] = lerp(this.cur[k], this.target[k], 1 - Math.pow(0.001, dt));
    // Smooth mouth amplitude (fast open, slower close).
    this.ampSmooth = this.amp > this.ampSmooth ? lerp(this.ampSmooth, this.amp, 0.6) : lerp(this.ampSmooth, this.amp, 0.25);
    // Gaze eases toward where the pointer last was, with gentle idle drift.
    this.gaze.x = lerp(this.gaze.x, this.gazeTarget.x * 0.4 + Math.sin(this.t * 0.6) * 0.12, 0.08);
    this.gaze.y = lerp(this.gaze.y, this.gazeTarget.y * 0.3 + Math.sin(this.t * 0.5) * 0.08, 0.08);
    // Blink scheduling.
    if (this.t > this.nextBlink) { this.blinkPhase = 0; this.nextBlink = this.t + 2 + Math.random() * 4; }
    if (this.blinkPhase !== undefined) {
      this.blinkPhase += dt * 9;
      this.blink = this.blinkPhase < 1 ? 1 - this.blinkPhase : Math.min(1, this.blinkPhase - 1);
      if (this.blinkPhase >= 2) { this.blink = 1; this.blinkPhase = undefined; }
    }
    this._draw();
    requestAnimationFrame(this._loop.bind(this));
  }

  _draw() {
    const ctx = this.ctx, S = this.S, e = this.cur;
    const cx = S / 2, cy = S / 2;
    const breathe = Math.sin(this.t * 1.1) * S * 0.004;
    const tilt = e.tilt + Math.sin(this.t * 0.4) * 0.015;
    ctx.clearRect(0, 0, S, S);
    ctx.save();
    ctx.translate(cx, cy + breathe);
    ctx.rotate(tilt);

    const skin = '#f3c9b0', skinShade = '#e6b59b';
    const hair = `hsl(${280}, 22%, 16%)`, hairLit = `hsl(${290}, 24%, 24%)`;
    const fw = S * 0.30, fh = S * 0.38; // face half-width/height

    // Hair back
    ctx.fillStyle = hair;
    ctx.beginPath(); ctx.ellipse(0, -S * 0.02, fw * 1.42, fh * 1.4, 0, 0, Math.PI * 2); ctx.fill();

    // Neck + shoulders hint
    ctx.fillStyle = skinShade;
    ctx.fillRect(-fw * 0.42, fh * 0.55, fw * 0.84, fh * 0.7);
    ctx.fillStyle = `hsl(${e.hue}, 40%, 42%)`;
    ctx.beginPath(); ctx.ellipse(0, fh * 1.45, fw * 1.6, fh * 0.9, 0, 0, Math.PI * 2); ctx.fill();

    // Face
    const grad = ctx.createLinearGradient(0, -fh, 0, fh);
    grad.addColorStop(0, skin); grad.addColorStop(1, skinShade);
    ctx.fillStyle = grad;
    ctx.beginPath(); ctx.moveTo(0, -fh);
    ctx.bezierCurveTo(fw, -fh, fw * 1.05, fh * 0.35, fw * 0.5, fh * 0.9);
    ctx.bezierCurveTo(fw * 0.2, fh * 1.12, -fw * 0.2, fh * 1.12, -fw * 0.5, fh * 0.9);
    ctx.bezierCurveTo(-fw * 1.05, fh * 0.35, -fw, -fh, 0, -fh);
    ctx.fill();

    // Blush
    if (e.blush > 0.05) {
      ctx.fillStyle = `rgba(255,120,150,${e.blush * 0.5})`;
      for (const s of [-1, 1]) { ctx.beginPath(); ctx.ellipse(s * fw * 0.55, fh * 0.28, fw * 0.22, fh * 0.12, 0, 0, Math.PI * 2); ctx.fill(); }
    }

    const eyeY = -fh * 0.02, eyeDx = fw * 0.46, eyeW = fw * 0.3, eyeH = fh * 0.17 * e.eye * this.blink;
    // Eyebrows
    ctx.strokeStyle = hairLit; ctx.lineWidth = S * 0.012; ctx.lineCap = 'round';
    for (const s of [-1, 1]) {
      const by = eyeY - fh * 0.26 - e.brow * fh * 0.1;
      ctx.beginPath();
      ctx.moveTo(s * (eyeDx - eyeW * 0.8), by + s * e.browAngle * fh * 0.06);
      ctx.quadraticCurveTo(s * eyeDx, by - fh * 0.03 - s * e.browAngle * fh * 0.04, s * (eyeDx + eyeW * 0.8), by - s * e.browAngle * fh * 0.02);
      ctx.stroke();
    }
    // Eyes
    for (const s of [-1, 1]) {
      const ex = s * eyeDx;
      ctx.save(); ctx.translate(ex, eyeY);
      // white
      ctx.fillStyle = '#fbf7f5';
      ctx.beginPath(); ctx.ellipse(0, 0, eyeW, Math.max(0.5, eyeH), 0, 0, Math.PI * 2); ctx.fill();
      if (eyeH > eyeW * 0.25) {
        // iris + pupil following gaze
        const ix = this.gaze.x * eyeW * 0.35, iy = this.gaze.y * eyeH * 0.4;
        ctx.save(); ctx.beginPath(); ctx.ellipse(0, 0, eyeW, eyeH, 0, 0, Math.PI * 2); ctx.clip();
        ctx.fillStyle = `hsl(${e.hue - 10}, 45%, 32%)`;
        ctx.beginPath(); ctx.arc(ix, iy, eyeW * 0.62, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = '#1a1020'; ctx.beginPath(); ctx.arc(ix, iy, eyeW * 0.32, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = '#fff9'; ctx.beginPath(); ctx.arc(ix - eyeW * 0.2, iy - eyeH * 0.3, eyeW * 0.12, 0, Math.PI * 2); ctx.fill();
        ctx.restore();
      }
      // upper lash line
      ctx.strokeStyle = '#2a1830'; ctx.lineWidth = S * 0.007;
      ctx.beginPath(); ctx.ellipse(0, 0, eyeW, Math.max(0.5, eyeH), 0, Math.PI, Math.PI * 2); ctx.stroke();
      ctx.restore();
    }

    // Nose
    ctx.strokeStyle = skinShade; ctx.lineWidth = S * 0.006;
    ctx.beginPath(); ctx.moveTo(-fw * 0.03, fh * 0.12); ctx.quadraticCurveTo(fw * 0.06, fh * 0.26, -fw * 0.04, fh * 0.3); ctx.stroke();

    // Mouth: curve = smile, plus live opening for speech.
    const my = fh * 0.56, mw = fw * 0.5;
    const open = Math.max(0, this.ampSmooth) * fh * 0.22 + Math.max(0, -e.smile) * fh * 0.02;
    const curve = e.smile * fh * 0.14;
    ctx.fillStyle = '#9e3b54';
    ctx.beginPath();
    ctx.moveTo(-mw, my);
    ctx.quadraticCurveTo(0, my + curve, mw, my);
    ctx.quadraticCurveTo(0, my + curve + open + Math.abs(curve) * 0.4, -mw, my);
    ctx.fill();
    if (open > fh * 0.03) { // teeth hint when open
      ctx.fillStyle = '#fff4f6';
      ctx.beginPath(); ctx.moveTo(-mw * 0.7, my); ctx.quadraticCurveTo(0, my + curve * 0.6, mw * 0.7, my);
      ctx.quadraticCurveTo(0, my + curve * 0.6 + open * 0.3, -mw * 0.7, my); ctx.fill();
    }

    // Hair front: a soft centre-parted fringe that sweeps down both sides of the face.
    const hg = ctx.createLinearGradient(0, -fh * 1.3, 0, fh * 0.6);
    hg.addColorStop(0, hairLit); hg.addColorStop(1, hair);
    ctx.fillStyle = hg;
    for (const s of [-1, 1]) {
      ctx.beginPath();
      ctx.moveTo(s * fw * 0.04, -fh * 1.1);                                   // top near the centre part
      ctx.bezierCurveTo(s * fw * 0.9, -fh * 1.15, s * fw * 1.22, -fh * 0.1, s * fw * 0.86, fh * 0.6); // outer sweep down the cheek
      ctx.bezierCurveTo(s * fw * 0.95, -fh * 0.15, s * fw * 0.78, -fh * 0.55, s * fw * 0.5, -fh * 0.72);
      ctx.bezierCurveTo(s * fw * 0.3, -fh * 0.82, s * fw * 0.14, -fh * 0.86, s * fw * 0.04, -fh * 0.78); // inner edge of the fringe
      ctx.closePath(); ctx.fill();
    }
    // A few strands catching light over the crown.
    ctx.strokeStyle = `hsl(295,22%,30%)`; ctx.lineWidth = S * 0.006; ctx.lineCap = 'round';
    for (const s of [-1, 1]) {
      ctx.beginPath();
      ctx.moveTo(s * fw * 0.06, -fh * 1.0);
      ctx.quadraticCurveTo(s * fw * 0.5, -fh * 0.95, s * fw * 0.66, -fh * 0.5);
      ctx.stroke();
    }

    ctx.restore();
  }
}
