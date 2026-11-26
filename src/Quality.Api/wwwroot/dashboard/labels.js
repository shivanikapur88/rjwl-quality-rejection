// Feature #8: printable machine QR labels (administrators). QR images come from GET /api/admin/machines/{id}/qr.svg.
'use strict';
(() => {
  const { api, wireLogin, el, text, make, session } = QR;
  const urls = [];

  wireLogin(async s => {
    if (s.role !== 'Administrator') { text(el('who'), 'Labels are for administrators.'); return; }
    el('page').hidden = false;
    try {
      for (const sh of await api('/api/shops')) el('shop').append(new Option(sh.shopName, sh.id));
      el('filters').addEventListener('submit', ev => { ev.preventDefault(); load(); });
      el('print').addEventListener('click', () => window.print());
      await load();
    } catch (e) { text(el('error'), e.message); el('error').hidden = false; }
  });

  async function load() {
    el('error').hidden = true;
    urls.splice(0).forEach(u => URL.revokeObjectURL(u));
    const host = el('labels'); host.replaceChildren();
    const q = new URLSearchParams({ shopId: el('shop').value });
    if (el('inactive').value !== 'true') q.set('isActive', 'true');
    try {
      const machines = await api('/api/admin/machines?' + q);
      if (!machines.length) { host.append(make('p', 'muted', 'No machines in this shop.')); return; }
      for (const m of machines) {
        const card = make('div', 'label');
        const img = make('img'); img.alt = `QR code for machine ${m.machineCode}`;
        card.append(img, make('div', 'code', m.machineCode), make('div', 'name', m.machineName), make('div', 'name', m.shopName + (m.isActive ? '' : ' · INACTIVE')));
        host.append(card);
        // The SVG needs the bearer token, so fetch it and show it through a blob: URL (allowed by the page's CSP).
        fetch(`/api/admin/machines/${m.id}/qr.svg`, { headers: { Authorization: 'Bearer ' + session().token } })
          .then(r => r.ok ? r.blob() : Promise.reject(new Error('QR ' + r.status)))
          .then(b => { const u = URL.createObjectURL(b); urls.push(u); img.src = u; })
          .catch(() => { img.replaceWith(make('div', 'error', 'QR code could not be loaded')); });
      }
    } catch (e) { text(el('error'), e.message); el('error').hidden = false; }
  }
})();
