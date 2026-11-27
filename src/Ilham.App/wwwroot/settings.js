// Settings drawer: who she calls you, health rules, the override password, her memory, and the vault.
'use strict';
const $ = s => document.querySelector(s);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const api = (u, opts) => fetch(u, opts).then(r => r.ok ? r.json().catch(() => ({})) : r.json().then(e => Promise.reject(e)));

export async function openSettings({ state, save, onProfile }) {
  $('#scrim').hidden = false; $('#drawer').hidden = false;
  const body = $('#drawerBody');
  body.innerHTML = '<p class="muted">Loading…</p>';
  const close = () => { $('#scrim').hidden = true; $('#drawer').hidden = true; };
  $('#scrim').onclick = close; $('#btnCloseDrawer').onclick = close;

  const [profile, mem, vault, hasPw, model] = await Promise.all([
    api('/api/profile'), api('/api/memory'), api('/api/vault'), api('/api/profile/has-password'), api('/api/model'),
  ]);

  body.innerHTML = `
    <div class="set-group">
      <h4>Her brain</h4>
      <div class="note">${model.ok ? '✅ ' + esc(model.status) : '⚠️ ' + esc(model.status)}</div>
      <div class="field"><label>Local model (Ollama)</label><input id="s-model" value="${esc(profile.model)}"></div>
      <div class="field"><label>Ollama URL</label><input id="s-url" value="${esc(profile.ollamaUrl)}"></div>
      <div class="field"><label>Language</label><select id="s-lang">
        <option value="mix"${profile.language === 'mix' ? ' selected' : ''}>Roman Urdu + English (mix)</option>
        <option value="urdu"${profile.language === 'urdu' ? ' selected' : ''}>Urdu (اردو)</option>
        <option value="english"${profile.language === 'english' ? ' selected' : ''}>English</option>
      </select></div>
    </div>

    <div class="set-group">
      <h4>About you</h4>
      <div class="field"><label>What she calls you</label><input id="s-name" value="${esc(profile.name)}"></div>
      <div class="field"><label>Your birthday</label><input id="s-bday" type="date" value="${profile.birthday || ''}"></div>
    </div>

    <div class="set-group">
      <h4>Health & protection</h4>
      <div class="field row2">
        <div class="field"><label>Break every (min)</label><input id="s-break" type="number" min="15" max="180" value="${profile.breakEveryMinutes}"></div>
        <div class="field"><label>Daily limit (min, 0=off)</label><input id="s-limit" type="number" min="0" max="1440" value="${profile.dailyLimitMinutes}"></div>
      </div>
      <div class="field row2">
        <div class="field"><label>Bedtime</label><input id="s-bed" type="time" value="${hm(profile.bedtime)}"></div>
        <div class="field"><label>Wake</label><input id="s-wake" type="time" value="${hm(profile.wakeTime)}"></div>
      </div>
      <div class="toggle"><span>Let her lock the screen if ignored</span><input id="s-lock" type="checkbox" ${profile.allowLock ? 'checked' : ''}></div>
      <div class="toggle"><span>Let her schedule shutdown at bedtime</span><input id="s-shut" type="checkbox" ${profile.allowShutdown ? 'checked' : ''}></div>
      <p class="note">She always gives a countdown, and you can always stop her with your override password.</p>
    </div>

    <div class="set-group">
      <h4>Override password</h4>
      <p class="note">${hasPw.has ? 'Set. Use it to snooze her when she locks or nags.' : 'Not set yet. Set one so you can always override her.'}</p>
      <div class="field"><input id="s-override" type="password" placeholder="${hasPw.has ? 'New override password' : 'Choose an override password'}"></div>
      <button class="btn" id="s-save-override">${hasPw.has ? 'Change' : 'Set'} override password</button>
    </div>

    <div class="set-group">
      <h4>Her vault <span class="hint">(you fill it — she never scans your browser or apps)</span></h4>
      <div id="vault-area"></div>
    </div>

    <div class="set-group">
      <h4>What she remembers</h4>
      <div class="field"><label>Add a fact about you</label><input id="s-fact" placeholder="e.g. I love nihari on Sundays"></div>
      <div class="chip-list" id="facts">${(mem.facts || []).map(f => chip('fact', f.id, f.text)).join('') || '<span class="hint">Nothing yet.</span>'}</div>
      <h4 style="margin-top:14px">Dates she celebrates</h4>
      <div class="field row2"><input id="s-occ-title" placeholder="Title (e.g. Ammi's birthday)"><input id="s-occ-date" type="date"></div>
      <div class="chip-list" id="occs">${(mem.occasions || []).map(o => chip('occ', o.id, `${esc(o.title)} · ${o.month}/${o.day}`)).join('') || '<span class="hint">None yet.</span>'}</div>
      <button class="btn danger" id="s-forget" style="margin-top:14px">Make her forget everything</button>
    </div>`;

  renderVault(vault);

  // Save profile on any change (debounced).
  let timer;
  const saveProfile = () => {
    clearTimeout(timer);
    timer = setTimeout(async () => {
      const bday = $('#s-bday').value;
      await api('/api/profile', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        name: $('#s-name').value || 'Jaan',
        birthday: bday || null,
        bedtime: $('#s-bed').value + ':00', wakeTime: $('#s-wake').value + ':00',
        breakEveryMinutes: +$('#s-break').value, dailyLimitMinutes: +$('#s-limit').value,
        allowLock: $('#s-lock').checked, allowShutdown: $('#s-shut').checked,
        ollamaUrl: $('#s-url').value, model: $('#s-model').value, language: $('#s-lang').value,
      }) });
      onProfile?.();
    }, 500);
  };
  body.querySelectorAll('input, select').forEach(el => el.addEventListener('change', saveProfile));
  $('#s-lang').addEventListener('change', () => { state.lang = { urdu: 'ur-PK', english: 'en-US', mix: 'ur-PK' }[$('#s-lang').value]; save('ilham.lang', state.lang); });

  $('#s-save-override').onclick = async () => {
    const pw = $('#s-override').value;
    if (pw.length < 4) return alert('At least 4 characters.');
    await api('/api/profile/password', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: pw }) });
    $('#s-override').value = ''; alert('Override password saved.');
  };

  $('#s-fact').addEventListener('keydown', async e => {
    if (e.key !== 'Enter' || !e.target.value.trim()) return;
    const f = await api('/api/memory/fact', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: e.target.value.trim() }) });
    if (f) $('#facts').insertAdjacentHTML('afterbegin', chip('fact', f.id, f.text));
    e.target.value = '';
  });
  $('#s-occ-date').addEventListener('change', async () => {
    const title = $('#s-occ-title').value.trim(), date = $('#s-occ-date').value;
    if (!title || !date) return;
    const [, mo, dy] = date.split('-').map(Number);
    const o = await api('/api/memory/occasion', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title, month: mo, day: dy, year: +date.slice(0, 4), kind: /birthday/i.test(title) ? 'birthday' : 'event' }) });
    if (o) $('#occs').insertAdjacentHTML('afterbegin', chip('occ', o.id, `${esc(title)} · ${mo}/${dy}`));
    $('#s-occ-title').value = '';
  });
  body.addEventListener('click', async e => {
    const x = e.target.closest('.chip button'); if (!x) return;
    const [kind, id] = x.dataset.k.split(':');
    await fetch(kind === 'fact' ? `/api/memory/fact/${id}` : `/api/memory/occasion/${id}`, { method: 'DELETE' });
    x.closest('.chip').remove();
  });
  $('#s-forget').onclick = async () => {
    if (!confirm('She will forget every fact, date and conversation. This cannot be undone. Sure?')) return;
    const pw = hasPw.has ? prompt('Override password:') : '';
    const res = await fetch('/api/memory/forget-all', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: pw }) });
    if (res.ok) { alert('Done. She starts fresh.'); openSettings({ state, save, onProfile }); }
    else alert('Wrong password.');
  };

  function renderVault(v) {
    const area = $('#vault-area');
    if (!v.isInitialized) {
      area.innerHTML = `<p class="note">Set a master password to start her vault. Only you know it — if you forget it, the secrets are gone for good.</p>
        <div class="field"><input id="v-init" type="password" placeholder="Master password"></div><button class="btn primary" id="v-init-btn">Create vault</button>`;
      $('#v-init-btn').onclick = async () => { if ($('#v-init').value.length < 6) return alert('At least 6 characters.'); await api('/api/vault/init', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: $('#v-init').value }) }); openSettings({ state, save, onProfile }); };
      return;
    }
    if (!v.isUnlocked) {
      area.innerHTML = `<p class="note">🔒 Locked.</p><div class="field"><input id="v-unlock" type="password" placeholder="Master password"></div><button class="btn" id="v-unlock-btn">Unlock</button>`;
      $('#v-unlock-btn').onclick = async () => { const r = await api('/api/vault/unlock', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: $('#v-unlock').value }) }); if (r.ok) openSettings({ state, save, onProfile }); else alert('Wrong password.'); };
      return;
    }
    area.innerHTML = `
      ${(v.entries || []).map(e => `<div class="vault-row" data-id="${e.id}"><span class="t">${esc(e.title)}${e.username ? ` · <span class="hint">${esc(e.username)}</span>` : ''}</span><button class="btn reveal-btn">reveal</button><button class="chip-del">✕</button></div>`).join('') || '<p class="hint">Empty.</p>'}
      <div class="field" style="margin-top:10px"><input id="v-title" placeholder="Title (e.g. Netflix)"></div>
      <div class="field row2"><input id="v-user" placeholder="Username (optional)"><input id="v-secret" type="password" placeholder="Secret"></div>
      <button class="btn primary" id="v-add">Add to vault</button>
      <button class="btn" id="v-lock" style="margin-left:6px">Lock</button>`;
    $('#v-add').onclick = async () => {
      if (!$('#v-title').value || !$('#v-secret').value) return;
      await api('/api/vault/put', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title: $('#v-title').value, secret: $('#v-secret').value, username: $('#v-user').value, note: null }) });
      openSettings({ state, save, onProfile });
    };
    $('#v-lock').onclick = async () => { await fetch('/api/vault/lock', { method: 'POST' }); openSettings({ state, save, onProfile }); };
    area.querySelectorAll('.reveal-btn').forEach(b => b.onclick = async () => {
      const id = b.closest('.vault-row').dataset.id;
      const r = await api(`/api/vault/${id}/reveal`);
      b.outerHTML = `<span class="reveal">${esc(r.secret)}</span>`;
    });
    area.querySelectorAll('.chip-del').forEach(b => b.onclick = async () => { await fetch(`/api/vault/${b.closest('.vault-row').dataset.id}`, { method: 'DELETE' }); b.closest('.vault-row').remove(); });
  }
}

const chip = (kind, id, text) => `<span class="chip">${esc(text)}<button data-k="${kind}:${id}">✕</button></span>`;
const hm = t => (t || '00:00:00').slice(0, 5);
