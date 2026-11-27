// Feature #8: printable machine QR labels (administrators). QR images come from GET /api/admin/machines/{id}/qr.svg.
'use strict';
(() => {
  const { api, wireLogin, el, text, make, session, clear } = QR;
  const urls = [];

  wireLogin(async s => {
    if (s.role !== 'Administrator') {
      // Shown in the page, not hidden in the header (a supervisor reaching this page sees why it is empty).
      const card = make('section', 'card narrow'); card.append(make('h1', '', 'Machine QR labels'), make('p', 'muted', 'Printing machine labels is for administrators. Ask an administrator to print or replace a label.'));
      document.querySelector('main').append(card); return;
    }
    el('page').hidden = false;
    try {
      for (const sh of await api('/api/shops')) el('shop').append(new Option(sh.shopName, sh.id));
      el('filters').addEventListener('submit', ev => { ev.preventDefault(); load(); });
      // Changing the shop or the inactive option shows the matching labels at once, so Print never prints a stale selection.
      el('shop').addEventListener('change', load); el('inactive').addEventListener('change', load);
      el('print').addEventListener('click', () => window.print());   // enabled only once every QR image has loaded
      await load();
    } catch (e) { text(el('error'), e.message); el('error').hidden = false; }
  });

  let loadSeq = 0;
  async function load() {
    const seq = ++loadSeq;   // a second quick "Show" must not add its labels to the first one's (labels of two shops)
    el('error').hidden = true;
    const q = new URLSearchParams({ shopId: el('shop').value });
    if (el('inactive').value !== 'true') q.set('isActive', 'true');
    try {
      const machines = await api('/api/admin/machines?' + q);
      if (seq !== loadSeq) return;
      urls.splice(0).forEach(u => URL.revokeObjectURL(u));
      const host = el('labels'); host.replaceChildren();
      const s = session();
      if (!s) { location.reload(); return; }   // signed out meanwhile: show the sign-in form
      if (!machines.length) { host.append(make('p', 'muted', 'No machines in this shop.')); return; }
      // Print would print blank labels while QR images are still loading: keep it disabled until all have settled.
      let pending = machines.length;
      el('print').disabled = true;
      const settled = () => { if (--pending === 0 && seq === loadSeq) el('print').disabled = false; };
      for (const m of machines) {
        const card = make('div', 'label');
        const img = make('img'); img.alt = `QR code for machine ${m.machineCode}`;
        card.append(img, make('div', 'code', m.machineCode), make('div', 'name', m.machineName), make('div', 'name', m.shopName + (m.isActive ? '' : ' · INACTIVE')));
        host.append(card);
        // The SVG needs the bearer token, so fetch it and show it through a blob: URL (allowed by the page's CSP).
        fetch(`/api/admin/machines/${m.id}/qr.svg`, { headers: { Authorization: 'Bearer ' + s.token } })
          .then(r => { if (r.status === 401) { clear(); location.reload(); throw new Error('session'); } return r.ok ? r.blob() : Promise.reject(new Error('QR ' + r.status)); })
          .then(b => new Promise(res => { const u = URL.createObjectURL(b); urls.push(u); img.onload = img.onerror = res; img.src = u; }))
          .catch(() => { img.replaceWith(make('div', 'error', 'QR code could not be loaded')); })
          .finally(settled);
      }
    } catch (e) { if (seq === loadSeq) { text(el('error'), e.message); el('error').hidden = false; } }
  }
})();
