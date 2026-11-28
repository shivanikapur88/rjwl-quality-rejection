// Shared by the dashboard pages: sign-in, token handling and API calls. No external libraries (plant networks may be offline).
'use strict';
const QR = (() => {
  const KEY = 'qr.dashboard.session';
  const session = () => { try { return JSON.parse(sessionStorage.getItem(KEY) || 'null'); } catch { return null; } };
  const save = s => { try { sessionStorage.setItem(KEY, JSON.stringify(s)); } catch { /* private mode: session lasts this page only */ } };
  const clear = () => { try { sessionStorage.removeItem(KEY); } catch { } };
  // No expiry check against this PC's clock (it may be wrong by hours): the server decides; a 401 clears the session.
  const valid = s => !!(s && s.token);

  async function api(path, opts = {}) {
    const s = session();
    const headers = Object.assign({ Accept: 'application/json' }, opts.headers || {});
    if (s && s.token) headers.Authorization = 'Bearer ' + s.token;
    if (opts.body && !(opts.body instanceof Blob)) headers['Content-Type'] = 'application/json';
    let r;
    try { r = await fetch(path, Object.assign({}, opts, { headers, cache: 'no-store' })); }
    catch { throw new Error('Cannot reach the server. Check the network and try again.'); }
    if (r.status === 401 && s) { clear(); location.reload(); throw new Error('Your session has expired.'); }
    if (!r.ok) {
      let msg = 'The server had a problem (' + r.status + '). Please try again.';
      try { const p = await r.json(); if (p && p.title) msg = p.title; } catch { }
      throw new Error(msg);
    }
    const type = r.headers.get('Content-Type') || '';
    return type.includes('json') ? r.json() : r.text();
  }

  async function login(username, password) {
    const r = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) });
    if (r.role !== 'Supervisor' && r.role !== 'Administrator') throw new Error('The dashboard is for supervisors and administrators.');
    save(r);
    return r;
  }

  const el = id => document.getElementById(id);
  const fmt = n => new Intl.NumberFormat().format(n);
  const text = (node, s) => { node.textContent = s; return node; };
  const make = (tag, cls, s) => { const n = document.createElement(tag); if (cls) n.className = cls; if (s !== undefined) n.textContent = s; return n; };

  function wireLogin(onReady) {
    const s = session();
    el('logout').addEventListener('click', () => { clear(); location.reload(); });
    if (valid(s)) { el('nav').hidden = false; text(el('who'), s.displayName + ' (' + s.role + ')'); if (s.role === 'Administrator' && el('labelsLink')) el('labelsLink').hidden = false; onReady(s); return; }
    clear();
    el('login').hidden = false;
    el('loginForm').addEventListener('submit', async ev => {
      ev.preventDefault();
      el('loginError').hidden = true;
      try { await login(el('user').value.trim(), el('pass').value); location.reload(); }
      catch (e) { text(el('loginError'), e.message); el('loginError').hidden = false; el('pass').focus(); el('pass').select(); }
    });
  }

  // ---- desktop app (installable web app) ----
  // The service worker keeps only the dashboard's own files (see sw.js); report data always comes from the server.
  if ('serviceWorker' in navigator && window.isSecureContext)
    window.addEventListener('load', () => { navigator.serviceWorker.register('sw.js').catch(() => { /* still works as a web page */ }); });
  // Edge and Chrome offer installation; show an "Install app" button where they do (hidden once installed).
  let installPrompt = null;
  window.addEventListener('beforeinstallprompt', ev => {
    ev.preventDefault(); installPrompt = ev;
    const b = document.getElementById('install'); if (b) b.hidden = false;
  });
  window.addEventListener('appinstalled', () => { const b = document.getElementById('install'); if (b) b.hidden = true; installPrompt = null; });
  document.addEventListener('DOMContentLoaded', () => {
    const b = document.getElementById('install');
    if (b) b.addEventListener('click', async () => { if (!installPrompt) return; installPrompt.prompt(); await installPrompt.userChoice; installPrompt = null; b.hidden = true; });
  });

  return { api, session, clear, wireLogin, el, fmt, text, make };
})();
