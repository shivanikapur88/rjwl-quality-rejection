// Service worker of the installable dashboard (desktop app). It only keeps the dashboard's OWN files (pages, styles,
// scripts, icons) so the window opens quickly and shows a clear message when the server cannot be reached.
// It never stores report data or anything under /api: the numbers always come live from the server, and nothing
// about rejections stays on a shared PC.
'use strict';
const VERSION = 'qr-dashboard-v1';
const SHELL = ['./', 'index.html', 'labels.html', 'dashboard.css', 'common.js', 'dashboard.js', 'labels.js', 'manifest.webmanifest',
  'icons/icon-192.png', 'icons/icon-512.png', 'icons/maskable-512.png', 'icons/icon.svg', 'icons/favicon-32.png'];

self.addEventListener('install', ev => {
  ev.waitUntil(caches.open(VERSION).then(c => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', ev => {
  ev.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== VERSION).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', ev => {
  const url = new URL(ev.request.url);
  if (ev.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/api/')) return;   // network only
  if (!url.pathname.startsWith('/dashboard/')) return;
  // Network first (so a new version is used as soon as the server has it), the saved copy only when offline.
  ev.respondWith(fetch(ev.request).then(res => {
    if (res.ok) { const copy = res.clone(); caches.open(VERSION).then(c => c.put(ev.request, copy)); }
    return res;
  }).catch(() => caches.match(ev.request, { ignoreSearch: true })));
});
