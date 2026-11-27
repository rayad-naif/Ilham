// Ilham — main UI wiring. Ties her face, voice, consensual webcam read, chat stream, command approvals,
// guardian alerts and settings together. Everything talks to the local backend only.
'use strict';
import { Avatar } from './avatar.js';
import { Listener, Voice, ToneMeter } from './voice.js';
import { Perception } from './perception.js';
import { openSettings } from './settings.js';

const $ = s => document.querySelector(s);
const state = {
  mood: 'neutral', speakOn: load('ilham.speak', true), micOn: false, camOn: false,
  lang: load('ilham.lang', 'ur-PK'), lastGuardianLevel: {}, name: 'Jaan',
};
function load(k, d) { try { const v = localStorage.getItem(k); return v === null ? d : JSON.parse(v); } catch { return d; } }
function save(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch {} }

// ── Face + voice ──
const avatar = new Avatar($('#face'));
const voice = new Voice({ onAmplitude: a => avatar.setAmplitude(a) });
voice.enabled = state.speakOn;
const tone = new ToneMeter();
const listener = new Listener({
  lang: state.lang,
  onResult: (text, final) => { $('#input').value = text; if (final && text) { $('#composer').requestSubmit(); } },
  onState: on => { state.micListening = on; $('#composer').classList.toggle('listening', on); },
});

// ── Chat ──
const messagesEl = $('#messages');
function addMessage(cls, text = '') {
  const el = document.createElement('div');
  el.className = `msg ${cls}`;
  el.textContent = text;
  messagesEl.appendChild(el);
  messagesEl.scrollTop = messagesEl.scrollHeight;
  return el;
}
function setMood(mood) {
  window.__setMood = m => { state.mood = m; avatar.setMood(m); document.querySelector("#moodBadge").textContent = m; };
  if (!mood) return;
  state.mood = mood;
  avatar.setMood(mood);
  $('#moodBadge').textContent = mood;
}

async function send(text) {
  text = text.trim();
  if (!text) return;
  addMessage('me', text);
  $('#input').value = '';
  const bubble = addMessage('her typing');
  let full = '';
  voice.cancel();
  busy = true;

  await streamChat(text, {
    onDelta: (delta, mood) => { full += delta; bubble.textContent = full; if (mood) setMood(mood); messagesEl.scrollTop = messagesEl.scrollHeight; },
    onDone: async (mood, pending) => {
      bubble.classList.remove('typing');
      setMood(mood);
      if (state.speakOn && full) voice.speak(full, state.lang, mood);
      for (const cmd of pending) renderCommand(cmd);
    },
    onError: () => { bubble.classList.remove('typing'); if (!full) bubble.textContent = '… (main abhi soch nahi paa rahi — model check karo settings mein)'; },
  });
  busy = false;
}

function streamChat(message, { onDelta, onDone, onError }) {
  return new Promise(resolve => {
    const es = new EventSource(`/api/chat?message=${encodeURIComponent(message)}`);
    es.addEventListener('delta', e => { const d = JSON.parse(e.data); onDelta(d.text, d.mood); });
    es.addEventListener('done', e => { const d = JSON.parse(e.data); es.close(); onDone(d.mood, d.pending || []); resolve(); });
    es.onerror = () => { es.close(); onError?.(); resolve(); };
  });
}

// ── She speaks first: poll for her unprompted lines (check-ins, teasing, nagging, demands, sulking) ──
let busy = false;
window.__tryInit = () => tryInitiative();
async function tryInitiative() {
  if (busy || document.hidden) return;
  busy = true;
  await new Promise(resolve => {
    const es = new EventSource('/api/initiative');
    let bubble = null, full = '';
    es.addEventListener('none', () => { es.close(); resolve(); });
    es.addEventListener('delta', e => {
      const d = JSON.parse(e.data);
      if (!bubble) bubble = addMessage('her');
      full += d.text; bubble.textContent = full; if (d.mood) setMood(d.mood);
      messagesEl.scrollTop = messagesEl.scrollHeight;
    });
    es.addEventListener('done', e => {
      const d = JSON.parse(e.data); es.close();
      setMood(d.mood);
      if (full) { if (state.speakOn) voice.speak(full, state.lang, d.mood); avatar.pulse(d.kind); }
      resolve();
    });
    es.onerror = () => { es.close(); resolve(); };
  });
  busy = false;
}

// ── Command approval cards ──
function renderCommand(cmd) {
  const card = document.createElement('div');
  card.className = `cmd-card ${cmd.risk}`;
  card.innerHTML = `
    <div class="risk ${cmd.risk}">${cmd.risk === 'safe' ? 'wants to run' : cmd.risk + ' — approve carefully'}</div>
    <code>${escapeHtml(cmd.shell)} › ${escapeHtml(cmd.command)}</code>
    ${cmd.reasons?.length ? `<div class="why">${cmd.reasons.map(escapeHtml).join(' · ')}</div>` : ''}
    <div class="row"><button class="approve">Run it</button><button class="reject">No</button></div>`;
  messagesEl.appendChild(card);
  messagesEl.scrollTop = messagesEl.scrollHeight;
  card.querySelector('.approve').onclick = async () => {
    card.querySelector('.row').innerHTML = '<span class="muted">running…</span>';
    const res = await fetch(`/api/command/${cmd.id}/approve`, { method: 'POST' }).then(r => r.json()).catch(() => null);
    if (res) {
      card.insertAdjacentHTML('beforeend', `<pre class="out">${escapeHtml((res.output || '').slice(0, 2000) || '(no output)')}</pre>`);
      if (res.reaction) { const b = addMessage('her', res.reaction); if (state.speakOn) voice.speak(res.reaction, state.lang, state.mood); }
    }
  };
  card.querySelector('.reject').onclick = async () => { await fetch(`/api/command/${cmd.id}/reject`, { method: 'POST' }); card.remove(); };
}
const escapeHtml = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// ── Controls ──
$('#composer').addEventListener('submit', e => { e.preventDefault(); send($('#input').value); });

$('#btnMic').addEventListener('click', () => {
  if (!listener.supported) return toast('Voice input needs Chrome or Edge.');
  state.micOn = !state.micOn;
  $('#btnMic').setAttribute('aria-pressed', state.micOn);
  if (state.micOn) listener.start(); else listener.stop();
});

$('#btnSpeak').addEventListener('click', () => {
  state.speakOn = !state.speakOn; voice.enabled = state.speakOn; save('ilham.speak', state.speakOn);
  $('#btnSpeak').setAttribute('aria-pressed', state.speakOn);
  $('#btnSpeak').textContent = state.speakOn ? '🔊' : '🔇';
  if (!state.speakOn) voice.cancel();
});

let perception = null;
$('#btnCam').addEventListener('click', async () => {
  if (state.camOn) { perception?.stop(); perception = null; state.camOn = false; $('#btnCam').setAttribute('aria-pressed', false); $('#camDot').hidden = true; return; }
  perception = new Perception($('#cam'), { onRead: sendPerception });
  try { await perception.start(); state.camOn = true; $('#btnCam').setAttribute('aria-pressed', true); $('#camDot').hidden = false; }
  catch { perception = null; toast('Camera permission was declined.'); }
});

let lastPerceive = 0;
async function sendPerception(report, faceReady) {
  const now = Date.now();
  if (now - lastPerceive < 4000) return; // don't flood her
  lastPerceive = now;
  if (state.micOn) { report.VoiceEnergy = tone.energy(); report.VoicePace = report.VoiceEnergy; }
  const r = await fetch('/api/perceive', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(report) }).then(r => r.json()).catch(() => null);
  if (r?.mood) setMood(r.mood);
}

$('#btnSettings').addEventListener('click', () => openSettings({ state, save, onProfile: refreshState }));

// ── Guardian + mood polling ──
async function refreshState() {
  const s = await fetch('/api/state').then(r => r.json()).catch(() => null);
  if (!s) return;
  state.name = s.name;
  $('#headerSub').textContent = `${s.name}'s companion`;
  if (!document.hasFocus || state.mood !== s.mood) setMood(s.mood);
  renderGuardian(s.guardian);
}

function renderGuardian(g) {
  const bar = $('#guardianBar');
  const top = Object.entries(g.levels || {}).sort((a, b) => b[1] - a[1])[0];
  if (!top) { bar.hidden = true; return; }
  const [concern, level] = top;
  // Only speak when the level rises (so she doesn't repeat herself every poll).
  const prev = state.lastGuardianLevel[concern] || 0;
  const labels = { Break: 'break lo', Bedtime: 'so jao', DailyLimit: 'screen time zyada ho gaya' };
  bar.hidden = false;
  bar.classList.toggle('alarm', level >= 4);
  bar.innerHTML = `<span>⚠️ ${state.name}, ${labels[concern] || 'khayal rakho'} — Ilham ${level >= 3 ? 'naraz ho rahi hai' : 'reminding you'} (step ${level}/5)</span>${level >= 4 ? '<button id="snoozeBtn">Override</button>' : ''}`;
  if (level > prev && level >= 2) {
    fetch(`/api/chat?message=${encodeURIComponent('[internal: remind me about my ' + concern + ', step ' + level + ']')}`); // let her speak it in chat
  }
  state.lastGuardianLevel[concern] = level;
  const sb = $('#snoozeBtn');
  if (sb) sb.onclick = async () => {
    const pw = prompt('Override password:');
    if (!pw) return;
    const res = await fetch('/api/guardian/snooze', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: pw, minutes: 30 }) });
    if (!res.ok) toast('Wrong password — she doesn\'t believe you. 😤');
  };
}

// ── Model status ──
async function refreshModel() {
  const m = await fetch('/api/model').then(r => r.json()).catch(() => null);
  const pill = $('#modelPill');
  if (!m) { pill.textContent = 'backend offline'; return; }
  pill.textContent = m.ok ? m.status : (m.demo ? 'demo mode · install Ollama for her real brain' : m.status);
  pill.className = 'model-pill ' + (m.ok ? 'ok' : 'demo');
}

function toast(msg) {
  const t = document.createElement('div');
  t.className = 'guardian-bar'; t.style.position = 'fixed'; t.style.bottom = '16px'; t.style.left = '16px'; t.style.zIndex = 9;
  t.textContent = msg; document.body.appendChild(t); setTimeout(() => t.remove(), 4000);
}

// ── Boot ──
$('#btnSpeak').textContent = state.speakOn ? '🔊' : '🔇';
$('#btnSpeak').setAttribute('aria-pressed', state.speakOn);
setMood('neutral');
refreshState(); refreshModel();
setInterval(refreshState, 15000);
setInterval(refreshModel, 30000);
// She checks whether she wants to say something on her own, every ~20s.
setInterval(tryInitiative, 20000);
avatar.startWander($('.face-pane'));

// A gentle hello once she's ready.
fetch('/api/memory').then(r => r.json()).then(d => {
  const greetings = ['Aa gaye? Main wait kar rahi thi. 💜', 'Hey. Kaise ho?', 'Finally! Bolो, kya chal raha hai?'];
  setTimeout(() => { const g = greetings[Math.floor(Math.random() * greetings.length)]; addMessage('her', g); if (state.speakOn) voice.speak(g, state.lang, 'happy'); setMood('happy'); }, 600);
}).catch(() => {});
