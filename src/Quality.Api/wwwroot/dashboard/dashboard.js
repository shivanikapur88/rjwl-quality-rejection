// Feature #6: read-only rejection dashboard. Data: GET /api/reports/calendar, /api/reports/totals, /api/shops, /api/corrections.
'use strict';
(() => {
  const { api, wireLogin, el, fmt, text, make } = QR;
  const SHIFT_SLOTS = ['--series-1', '--series-2', '--series-3', '--series-4'];   // fixed order by shift sort order, never by rank
  let shifts = [];

  const isoDay = d => d.toISOString().slice(0, 10);
  const addDays = (iso, n) => { const d = new Date(iso + 'T00:00:00Z'); d.setUTCDate(d.getUTCDate() + n); return isoDay(d); };
  const shortDay = iso => new Date(iso + 'T00:00:00Z').toLocaleDateString(undefined, { day: 'numeric', month: 'short', timeZone: 'UTC' });

  wireLogin(async () => {
    el('dash').hidden = false;
    try {
      const [cal, shops] = await Promise.all([api('/api/reports/calendar'), api('/api/shops')]);
      shifts = cal.shifts;
      for (const s of shops) el('shop').append(new Option(s.shopName, s.id));
      for (const s of shifts) el('shift').append(new Option(`${s.shiftName} (from ${s.startTime})`, s.shiftCode));
      el('to').value = cal.productionDate; el('from').value = addDays(cal.productionDate, -6);
      el('from').max = el('to').max = cal.productionDate;
      text(el('status'), `Today's production date: ${shortDay(cal.productionDate)}, current shift ${cal.currentShift} (${cal.timeZone}).`);
      document.querySelectorAll('.quick button').forEach(b => b.addEventListener('click', () => {
        el('to').value = cal.productionDate; el('from').value = addDays(cal.productionDate, 1 - Number(b.dataset.days)); load();
      }));
      el('filters').addEventListener('submit', ev => { ev.preventDefault(); load(); });
      await load();
    } catch (e) { showError(e); }
  });

  function showError(e) { text(el('error'), e.message); el('error').hidden = false; }

  async function load() {
    el('error').hidden = true;
    const q = new URLSearchParams({ fromDate: el('from').value, toDate: el('to').value, top: '10' });
    if (el('shop').value) q.set('shopId', el('shop').value);
    if (el('shift').value) q.set('shiftCode', el('shift').value);
    document.body.classList.add('busy');
    try {
      const [r, pending] = await Promise.all([api('/api/reports/totals?' + q), api('/api/corrections?status=Pending').catch(() => null)]);
      render(r);
      text(el('tPending'), pending ? fmt(pending.length) : '–');
    } catch (e) { showError(e); }
    finally { document.body.classList.remove('busy'); }
  }

  function render(r) {
    text(el('tParts'), fmt(r.total.quantity));
    text(el('tEntries'), fmt(r.total.entries));
    el('incomplete').hidden = r.unavailableShops.length === 0;
    text(el('incomplete'), r.unavailableShops.length ? `Incomplete: the entries of ${r.unavailableShops.join(', ')} could not be read right now. Totals below leave them out.` : '');
    renderTrend(r);
    renderPareto(r);
    renderTable(el('machines'), ['Machine', 'Shop', 'Parts', 'Entries'], r.topMachines.map(m => [`${m.machineCode} – ${m.machineName}`, m.shopCode, fmt(m.quantity), fmt(m.entries)]), 'No rejections in this selection.');
    renderTable(el('shops'), ['Shop', 'Parts', 'Entries'], r.shops.map(s => [s.shopName, fmt(s.quantity), fmt(s.entries)]), 'No shops.');
  }

  // ---- stacked bars: one bar per production day, segments = shifts (fixed slot per shift) ----
  function renderTrend(r) {
    const host = el('trend'); host.replaceChildren();
    const days = []; for (let d = r.fromDate; d <= r.toDate; d = addDays(d, 1)) days.push(d);
    const shown = r.shiftCode ? shifts.filter(s => s.shiftCode === r.shiftCode) : shifts;
    const val = (d, s) => { const x = r.byDayShift.find(t => t.productionDate === d && t.shiftCode === s); return x ? x.quantity : 0; };
    const totals = days.map(d => shown.reduce((a, s) => a + val(d, s.shiftCode), 0));
    const max = Math.max(1, ...totals);
    const nice = niceMax(max);

    // legend (always present for >= 2 series) - text stays in text ink, swatch carries identity
    const legend = el('legend'); legend.replaceChildren();
    shown.forEach(s => { const i = make('span', 'key'); const sw = make('span', 'swatch'); sw.style.background = `var(${slot(s)})`; i.append(sw, document.createTextNode(`${s.shiftName}`)); legend.append(i); });
    legend.hidden = shown.length < 2;

    const W = Math.max(320, host.clientWidth || 640), H = 240, padL = 44, padB = 28, padT = 8, padR = 8;
    const plotW = W - padL - padR, plotH = H - padT - padB;
    const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, width: '100%', height: H });
    for (let i = 0; i <= 4; i++) {   // recessive grid
      const y = padT + plotH - (plotH * i) / 4;
      svg.append(svgEl('line', { x1: padL, x2: W - padR, y1: y, y2: y, class: 'grid' }));
      const t = svgEl('text', { x: padL - 6, y: y + 4, class: 'axis', 'text-anchor': 'end' }); t.textContent = fmt(Math.round((nice * i) / 4)); svg.append(t);
    }
    const band = plotW / days.length, barW = Math.max(4, Math.min(48, band * 0.62));
    const labelEvery = Math.ceil(days.length / Math.max(1, Math.floor(plotW / 56)));
    days.forEach((d, i) => {
      const x = padL + band * i + (band - barW) / 2;
      let y = padT + plotH;
      shown.forEach((s, k) => {
        const v = val(d, s.shiftCode); if (!v) return;
        const h = (v / nice) * plotH;
        const top = k === shown.length - 1 || shown.slice(k + 1).every(n => !val(d, n.shiftCode));
        const seg = svgEl('path', { d: barPath(x, y - h, barW, Math.max(1, h - 2), top ? 4 : 0), class: 'mark' });   // 2px surface gap
        seg.style.fill = `var(${slot(s)})`;   // CSSOM, not a presentation attribute: var() works and the CSP allows it
        hover(seg, `${shortDay(d)} · ${s.shiftName}: ${fmt(v)} parts`);
        svg.append(seg);
        y -= h;
      });
      // invisible hit target the full column height, bigger than the mark
      const hit = svgEl('rect', { x: padL + band * i, y: padT, width: band, height: plotH, class: 'hit' });
      hover(hit, `${shortDay(d)}: ${fmt(totals[i])} parts` + (shown.length > 1 ? ' — ' + shown.map(s => `${s.shiftCode} ${fmt(val(d, s.shiftCode))}`).join(', ') : ''));
      svg.insertBefore(hit, svg.firstChild);
      if (i % labelEvery === 0) { const t = svgEl('text', { x: padL + band * i + band / 2, y: H - 8, class: 'axis', 'text-anchor': 'middle' }); t.textContent = shortDay(d); svg.append(t); }
    });
    host.append(svg);
    renderTable(el('trendTable'), ['Production day', ...shown.map(s => s.shiftName), 'Total'],
      days.map((d, i) => [shortDay(d), ...shown.map(s => fmt(val(d, s.shiftCode))), fmt(totals[i])]), 'No data.');
  }

  // ---- ranked horizontal bars (single series): value + share labels at the bar end ----
  function renderPareto(r) {
    const host = el('pareto'); host.replaceChildren();
    if (!r.topPhenomena.length) { host.append(make('p', 'muted', 'No rejections in this selection.')); return; }
    const max = Math.max(...r.topPhenomena.map(p => p.quantity));
    for (const p of r.topPhenomena) {
      const row = make('div', 'barrow');
      const name = make('div', 'barname', p.phenomenonName);
      const track = make('div', 'bartrack');
      const bar = make('div', 'bar'); bar.style.width = `${Math.max(2, (p.quantity / max) * 100)}%`;
      const share = r.total.quantity ? Math.round((p.quantity / r.total.quantity) * 100) : 0;
      const val = make('div', 'barval', `${fmt(p.quantity)} · ${share}%`);
      hover(row, `${p.phenomenonName} (${p.shopCode}): ${fmt(p.quantity)} parts in ${fmt(p.entries)} entries, ${share}% of rejected parts`);
      track.append(bar); row.append(name, track, val); host.append(row);
    }
  }

  function renderTable(host, head, rows, empty) {
    host.replaceChildren();
    if (!rows.length) { host.append(make('p', 'muted', empty)); return; }
    const t = make('table'); const tr = make('tr');
    head.forEach((h, i) => { const th = make('th', i > 0 && i >= head.length - 2 ? 'num' : '', h); th.scope = 'col'; tr.append(th); });
    const thead = make('thead'); thead.append(tr); t.append(thead);
    const tb = make('tbody');
    rows.forEach(r => { const row = make('tr'); r.forEach((c, i) => row.append(make('td', i > 0 && i >= head.length - 2 ? 'num' : '', c))); tb.append(row); });
    t.append(tb); host.append(t);
  }

  const slot = s => SHIFT_SLOTS[Math.max(0, shifts.findIndex(x => x.shiftCode === s.shiftCode)) % SHIFT_SLOTS.length];
  function niceMax(v) { const p = Math.pow(10, Math.floor(Math.log10(v))); for (const m of [1, 2, 2.5, 5, 10]) if (m * p >= v) return m * p; return 10 * p; }
  function barPath(x, y, w, h, r) {   // rounded top corners only, anchored to the baseline
    r = Math.min(r, w / 2, h);
    return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`;
  }
  function svgEl(tag, attrs) { const n = document.createElementNS('http://www.w3.org/2000/svg', tag); for (const k in attrs) n.setAttribute(k, attrs[k]); return n; }
  function hover(node, label) {
    const tip = el('tip');
    node.addEventListener('mousemove', ev => { tip.textContent = label; tip.hidden = false; tip.style.left = `${ev.clientX + 12}px`; tip.style.top = `${ev.clientY + 12}px`; });
    node.addEventListener('mouseleave', () => { tip.hidden = true; });
    node.setAttribute('aria-label', label);
  }
  window.addEventListener('resize', () => { /* charts are redrawn on the next Show */ });
})();
