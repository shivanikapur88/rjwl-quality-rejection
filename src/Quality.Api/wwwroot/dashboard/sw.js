// Service worker of the installable dashboard (desktop app). It only keeps the dashboard's OWN files (pages, styles,
// scripts, icons) so the window opens quickly and shows a clear message when the server cannot be reached.
// It never stores report data or anything under /api: the numbers always come live from the server, and nothing
// about rejections stays on a shared PC.
'use strict';
const VERSION = 'qr-dashboard-v2';   // raise after changing any dashboard file (old saved copies are then dropped)
const SHELL = ['./', 'index.html', 'labels.html', 'dashboard.css', 'common.js', 'dashboard.js', 'labels.js', 'manifest.webmanifest',
  'icons/icon-192.png', 'icons/icon-512.png', 'icons/maskable-512.png', 'icons/icon.svg', 'icons/favicon-32.png'];

// Path on the server -> the one cache key for it ('/dashboard/' and '/dashboard/index.html' are the same page).
const SHELL_PATHS = new Map(SHELL.map(f => [new URL(f, self.location).pathname, new URL(f === './' ? 'index.html' : f, self.location).href]));

self.addEventListener('install', ev => {
  ev.waitUntil(caches.open(VERSION).then(c => c.addAll([...new Set(SHELL_PATHS.values())])).then(() => self.skipWaiting()));
});

self.addEventListener('activate', ev => {
  ev.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== VERSION).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', ev => {
  const url = new URL(ev.request.url);
  if (ev.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/api/')) return;   // network only
  const key = SHELL_PATHS.get(url.pathname);
  if (!key) return;   // only the files listed above are ever saved (one copy each, whatever the query string)
  // Network first (so a new version is used as soon as the server has it). The saved copy is used when the server
  // cannot be reached OR answers with a server error (5xx, e.g. during a deploy or an IIS app-pool restart).
  ev.respondWith(fetch(ev.request).then(res => {
    if (res.ok) { const copy = res.clone(); ev.waitUntil(caches.open(VERSION).then(c => c.put(key, copy))); return res; }
    if (res.status >= 500) return caches.match(key).then(saved => saved || res);
    return res;
  }, () => caches.match(key).then(saved => saved || Response.error())));
});
