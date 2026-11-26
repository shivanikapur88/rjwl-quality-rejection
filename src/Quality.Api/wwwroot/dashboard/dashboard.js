// Rejection dashboard (read-only for supervisors; administrators can also upload production counts).
// Round 2: totals, trend by shift, top phenomena/machines, shops.
// Round 3: drill-down (click a day, phenomenon, machine or shop; filters shown as removable chips and kept in the URL),
//          comparison with the previous period of the same length, Pareto with cumulative share, entries behind the
//          selection, rejection rate where production counts exist, and production-count upload for administrators.
'use strict';
(() => {
  const { api, wireLogin, el, fmt, text, make, session } = QR;
  const SHIFT_SLOTS = ['--series-1', '--series-2', '--series-3', '--series-4'];   // fixed order by shift sort order, never by rank
  let shifts = [], cal = null, lastReport = null;
  const drill = { machineId: null, machineLabel: null, phenomenonId: null, phenomenonLabel: null };
  const COLLAPSED_PARETO = 15, COLLAPSED_ENTRIES = 20;
  let showAllPareto = false, showAllEntries = false, lastEntries = null;
  let rangeBeforeDay = null;   // the date range in use before a day was clicked, restored when the "Day" chip is removed
  let lastHash = '';

  const isoDay = d => d.toISOString().slice(0, 10);
  const addDays = (iso, n) => { const d = new Date(iso + 'T00:00:00Z'); d.setUTCDate(d.getUTCDate() + n); return isoDay(d); };
  const shortDay = iso => new Date(iso + 'T00:00:00Z').toLocaleDateString(undefined, { day: 'numeric', month: 'short', timeZone: 'UTC' });
  const pct = (x, digits = 1) => (x * 100).toFixed(digits) + '%';
  /** A real calendar day as YYYY-MM-DD (rejects 2026-02-31 and 2026-13-45, which the date pattern alone accepts). */
  const validDay = d => /^\d{4}-\d{2}-\d{2}$/.test(d || '') && !isNaN(Date.parse(d + 'T00:00:00Z')) && isoDay(new Date(d + 'T00:00:00Z')) === d;

  // ---- URL state: #from=..&to=..&shop=..&shift=..&m=..&ml=..&p=..&pl=.. (shareable links) ----
  function readHash() {
    const h = new URLSearchParams(location.hash.slice(1));
    return { from: h.get('from'), to: h.get('to'), shop: h.get('shop'), shift: h.get('shift'),
      m: h.get('m'), ml: h.get('ml'), p: h.get('p'), pl: h.get('pl') };
  }
  function writeHash() {
    const h = new URLSearchParams({ from: el('from').value, to: el('to').value });
    if (el('shop').value) h.set('shop', el('shop').value);
    if (el('shift').value) h.set('shift', el('shift').value);
    if (drill.machineId) { h.set('m', drill.machineId); h.set('ml', drill.machineLabel || ''); }
    if (drill.phenomenonId) { h.set('p', drill.phenomenonId); h.set('pl', drill.phenomenonLabel || ''); }
    lastHash = '#' + h.toString();
    history.replaceState(null, '', lastHash);
  }

  /** Applies the URL state (on start, and when a shared link is pasted into a tab that already shows the dashboard). */
  function applyHash() {
    const h = readHash();
    el('to').value = validDay(h.to) ? h.to : cal.productionDate;
    el('from').value = validDay(h.from) && h.from <= el('to').value ? h.from : addDays(el('to').value, -6);
    el('shop').value = h.shop && [...el('shop').options].some(o => o.value === h.shop) ? h.shop : '';
    el('shift').value = h.shift && [...el('shift').options].some(o => o.value === h.shift) ? h.shift : '';
    // Labels from the link are only placeholders: after loading they are replaced by the names the report returns.
    drill.machineId = /^\d+$/.test(h.m || '') ? h.m : null; drill.machineLabel = drill.machineId ? h.ml : null;
    drill.phenomenonId = /^\d+$/.test(h.p || '') ? h.p : null; drill.phenomenonLabel = drill.phenomenonId ? h.pl : null;
    rangeBeforeDay = null;
  }

  wireLogin(async s => {
    el('dash').hidden = false;
    if (s.role === 'Administrator') { el('prodCard').hidden = false; wireUpload(); }
    try {
      const [c, shops] = await Promise.all([api('/api/reports/calendar'), api('/api/shops')]);
      cal = c; shifts = c.shifts;
      for (const sh of shops) el('shop').append(new Option(sh.shopName, sh.id));
      for (const sh of shifts) el('shift').append(new Option(`${sh.shiftName} (from ${sh.startTime})`, sh.shiftCode));
      el('from').max = el('to').max = c.productionDate;
      applyHash();
      window.addEventListener('hashchange', () => { if (location.hash !== lastHash) { applyHash(); load(); } });
      text(el('status'), `Today's production date: ${shortDay(c.productionDate)}, current shift ${c.currentShift} (${c.timeZone}).`);
      document.querySelectorAll('.quick button').forEach(b => b.addEventListener('click', () => {
        el('to').value = c.productionDate; el('from').value = addDays(c.productionDate, 1 - Number(b.dataset.days)); rangeBeforeDay = null; load();
      }));
      el('filters').addEventListener('submit', ev => { ev.preventDefault(); rangeBeforeDay = null; load(); });
      await load();
    } catch (e) { showError(e); }
  });

  function showError(e) { text(el('error'), e.message); el('error').hidden = false; }

  function query(extra) {
    const q = new URLSearchParams({ fromDate: el('from').value, toDate: el('to').value });
    if (el('shop').value) q.set('shopId', el('shop').value);
    if (el('shift').value) q.set('shiftCode', el('shift').value);
    if (drill.machineId) q.set('machineId', drill.machineId);
    if (drill.phenomenonId) q.set('phenomenonId', drill.phenomenonId);
    for (const k in extra) q.set(k, extra[k]);
    return q;
  }

  let loadSeq = 0;
  /** focusChips: after a drill-down or chip removal, keyboard focus moves to the filter chips (the clicked control is rebuilt). */
  async function load(focusChips = false) {
    el('error').hidden = true;
    el('tip').hidden = true;   // the hovered element is about to be rebuilt, so its mouseleave never fires
    writeHash();
    renderChips();
    const seq = ++loadSeq;
    document.body.classList.add('busy');
    try {
      const countQ = 'status=Pending' + (el('shop').value ? '&shopId=' + el('shop').value : '');
      const [r, pending, entries] = await Promise.all([
        api('/api/reports/totals?' + query({ top: '50', compare: 'true' })),
        api('/api/corrections/count?' + countQ).catch(() => null),
        api('/api/reports/entries?' + query({ limit: '100' })).catch(e => ({ error: e })),
      ]);
      if (seq !== loadSeq) return;   // a later click started another load
      lastReport = r; showAllPareto = false; showAllEntries = false;
      // Chip labels from a shared link are replaced by the names in the report (a link cannot relabel another machine).
      const m = drill.machineId && r.topMachines.find(x => String(x.machineId) === drill.machineId);
      if (m) drill.machineLabel = m.machineCode;
      const p = drill.phenomenonId && r.topPhenomena.find(x => String(x.phenomenonId) === drill.phenomenonId);
      if (p) drill.phenomenonLabel = p.phenomenonName;
      if (m || p) { writeHash(); renderChips(); }
      render(r);
      text(el('tPending'), pending === null ? '–' : fmt(pending));
      renderEntries(entries);
      text(el('announce'), `Showing ${fmt(r.total.quantity)} rejected parts` + (el('chips').childElementCount ? `, filtered by ${[...el('chips').children].map(c => c.dataset.label).join(', ')}` : '') + '.');
      if (focusChips) (el('chips').querySelector('button') || el('chips')).focus();
    } catch (e) { if (seq === loadSeq) showError(e); }   // an error of a superseded load must not cover the newer view
    finally { if (seq === loadSeq) document.body.classList.remove('busy'); }
  }

  // ---- drill-down chips ----
  function renderChips() {
    const host = el('chips'); host.replaceChildren();
    const chip = (label, onRemove) => {
      const b = make('button', 'chip'); b.type = 'button';
      b.append(document.createTextNode(label + ' '), make('span', 'chip-x', '×'));
      b.setAttribute('aria-label', 'Remove filter ' + label); b.dataset.label = label;
      b.addEventListener('click', () => { onRemove(); load(true); });
      host.append(b);
    };
    if (el('from').value && el('from').value === el('to').value) chip(`Day: ${shortDay(el('from').value)}`, () => {
      if (rangeBeforeDay) { el('from').value = rangeBeforeDay.from; el('to').value = rangeBeforeDay.to; rangeBeforeDay = null; }
      else el('from').value = addDays(el('to').value, -6);
    });
    if (el('shop').value) chip(`Shop: ${el('shop').selectedOptions[0].text}`, () => { el('shop').value = ''; });
    if (el('shift').value) chip(`Shift ${el('shift').value}`, () => { el('shift').value = ''; });
    if (drill.machineId) chip(`Machine: ${drill.machineLabel || drill.machineId}`, () => { drill.machineId = drill.machineLabel = null; });
    if (drill.phenomenonId) chip(`Phenomenon: ${drill.phenomenonLabel || drill.phenomenonId}`, () => { drill.phenomenonId = drill.phenomenonLabel = null; });
    host.hidden = host.childElementCount === 0;
  }

  // ---- comparison text ----
  function delta(now, prev, r, noun) {
    if (prev === null || prev === undefined) return '';
    const period = `${shortDay(r.previousFromDate)}–${shortDay(r.previousToDate)}`;
    if (prev === 0) return now === 0 ? `same as ${period} (0)` : `${fmt(now)} more than ${period} (0 before)`;
    const ch = Math.round(((now - prev) / prev) * 100);   // whole percent; under 0.5% counts as no change
    if (ch === 0) return `about the same as ${period} (${fmt(prev)})`;
    return `${ch > 0 ? '▲' : '▼'} ${Math.abs(ch)}% ${ch > 0 ? 'more' : 'fewer'} ${noun} than ${period} (${fmt(prev)})`;
  }
  const shortDelta = (now, prev) => {
    if (prev === null || prev === undefined) return '';
    if (prev === 0) return now ? 'new' : '–';
    const ch = Math.round(((now - prev) / prev) * 100);
    return ch === 0 ? '＝ 0%' : (ch > 0 ? '▲ ' : '▼ ') + Math.abs(ch) + '%';
  };

  function rateText(rate) {
    if (!rate || rate.rate === null || rate.rate === undefined) return { value: '–', note: rate ? 'Production counts exist but show 0 produced.' : 'No production data for this selection.' };
    return { value: pct(rate.rate, 2), note: `${fmt(rate.rejectedWithProduction)} rejected of ${fmt(rate.produced)} produced · based on ${pct(rate.coverage ?? 0, 0)} of rejected parts` };
  }

  function render(r) {
    text(el('tParts'), fmt(r.total.quantity));
    text(el('tEntries'), fmt(r.total.entries));
    text(el('dParts'), delta(r.total.quantity, r.total.previousQuantity, r, 'rejected parts'));
    text(el('dEntries'), delta(r.total.entries, r.total.previousEntries, r, 'entries'));
    const rt = rateText(r.total.rate);
    text(el('tRate'), rt.value); text(el('dRate'), rt.note);
    el('incomplete').hidden = r.unavailableShops.length === 0;
    text(el('incomplete'), r.unavailableShops.length ? `Incomplete: the entries of ${r.unavailableShops.join(', ')} could not be read right now. Totals below leave them out.` : '');
    renderTrend(r);
    renderPareto(r);
    renderTable(el('machines'), ['Machine', 'Shop', 'Parts', 'vs prev.', 'Produced', 'Rate'],
      r.topMachines.slice(0, 15).map(m => ({
        cells: [`${m.machineCode} – ${m.machineName}`, m.shopCode, fmt(m.quantity), shortDelta(m.quantity, m.previousQuantity),
          m.rate ? fmt(m.rate.produced) : '–', m.rate && m.rate.rate !== null ? pct(m.rate.rate, 2) : '–'],
        label: `Show only machine ${m.machineCode}`,
        onClick: () => { drill.machineId = String(m.machineId); drill.machineLabel = m.machineCode; load(true); },
      })), 'No rejections in this selection.', 2);
    renderTable(el('shops'), ['Shop', 'Parts', 'Entries', 'Rate'],
      r.shops.map(s => ({
        cells: [s.shopName, fmt(s.quantity), fmt(s.entries), s.rate && s.rate.rate !== null ? pct(s.rate.rate, 2) : '–'],
        label: `Show only ${s.shopName}`,
        onClick: () => { el('shop').value = String(s.shopId); load(true); },
      })), 'No shops.', 1);
  }

  // ---- stacked bars: one bar per production day, segments = shifts (fixed slot per shift); click = that day ----
  function renderTrend(r) {
    const host = el('trend'); host.replaceChildren();
    const days = []; for (let d = r.fromDate; d <= r.toDate; d = addDays(d, 1)) days.push(d);
    const known = shifts.map(s => s.shiftCode);
    const extra = [...new Set(r.byDayShift.map(t => t.shiftCode))].filter(c => !known.includes(c)).sort()
      .map(c => ({ shiftCode: c, shiftName: `Shift ${c} (retired)` }));
    const shown = [...shifts, ...extra].filter(s => !r.shiftCode || s.shiftCode === r.shiftCode)
      .filter(s => known.includes(s.shiftCode) || r.byDayShift.some(t => t.shiftCode === s.shiftCode));
    const val = (d, s) => { const x = r.byDayShift.find(t => t.productionDate === d && t.shiftCode === s); return x ? x.quantity : 0; };
    const totals = days.map(d => shown.reduce((a, s) => a + val(d, s.shiftCode), 0));
    const nice = niceMax(Math.max(1, ...totals));

    const legend = el('legend'); legend.replaceChildren();
    shown.forEach(s => { const i = make('span', 'key'); const sw = make('span', 'swatch'); sw.style.background = `var(${slot(s)})`; i.append(sw, document.createTextNode(s.shiftName)); legend.append(i); });
    legend.hidden = shown.length < 2;

    const W = Math.max(320, host.clientWidth || 640), H = 240, padL = 44, padB = 28, padT = 8, padR = 8;
    const plotW = W - padL - padR, plotH = H - padT - padB;
    const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, width: '100%', height: H });
    for (let i = 0; i <= 4; i++) {
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
        const seg = svgEl('path', { d: barPath(x, y - h, barW, Math.max(1, h - 2), top ? 4 : 0), class: 'mark' });
        seg.style.fill = `var(${slot(s)})`;
        hover(seg, `${shortDay(d)} · ${s.shiftName}: ${fmt(v)} parts`);
        svg.append(seg);
        y -= h;
      });
      // Transparent hit area over the whole day column, ON TOP of the bars, so clicking a bar works. Mouse only: the
      // keyboard and screen-reader path is the "Show as table" table, whose day buttons do the same.
      const hit = svgEl('rect', { x: padL + band * i, y: padT, width: band, height: plotH, class: 'hit' });
      hover(hit, `${shortDay(d)}: ${fmt(totals[i])} parts` + (shown.length > 1 ? ' — ' + shown.map(s => `${s.shiftCode} ${fmt(val(d, s.shiftCode))}`).join(', ') : '') + ' (click to show this day)');
      hit.addEventListener('click', () => pickDay(d));
      svg.append(hit);
      if (i % labelEvery === 0) { const t = svgEl('text', { x: padL + band * i + band / 2, y: H - 8, class: 'axis', 'text-anchor': 'middle' }); t.textContent = shortDay(d); svg.append(t); }
    });
    svg.setAttribute('aria-hidden', 'true');   // the container's role="img" label describes it; the table holds the numbers
    host.append(svg);
    renderTable(el('trendTable'), ['Production day', ...shown.map(s => s.shiftName), 'Total'],
      days.map((d, i) => ({ cells: [shortDay(d), ...shown.map(s => fmt(val(d, s.shiftCode))), fmt(totals[i])],
        label: `Show only ${shortDay(d)}`, onClick: days.length > 1 ? () => pickDay(d) : null })), 'No data.', 1);
  }
  function pickDay(d) {
    if (el('from').value !== el('to').value) rangeBeforeDay = { from: el('from').value, to: el('to').value };
    el('from').value = el('to').value = d; load(true);
  }

  // ---- Pareto: ranked bars (single series) with share and cumulative share as text, 80% marker ----
  function renderPareto(r) {
    const host = el('pareto'); host.replaceChildren();
    const list = r.topPhenomena;
    if (!list.length) { text(el('paretoNote'), ''); host.append(make('p', 'muted', 'No rejections in this selection.')); return; }
    const total = r.total.quantity || 1, max = list[0].quantity;
    let cum = 0, vital = 0;
    for (const p of list) { if (cum / total < 0.8) vital++; cum += p.quantity; }
    const cut = list.slice(0, vital).reduce((a, p) => a + p.quantity, 0) / total;
    text(el('paretoNote'), list.length === 1 ? 'One phenomenon causes all rejected parts in this selection.'
      : `${vital} of ${r.phenomenaCount} phenomena cause ${pct(cut, 0)} of rejected parts.`);
    const head = make('div', 'prow phead');
    head.append(make('div', '', '#'), make('div', '', 'Phenomenon'), make('div', '', ''), make('div', 'num', 'Parts'), make('div', 'num', 'Share'), make('div', 'num', 'Cumulative'), make('div', 'num', 'vs prev.'));
    host.append(head);
    cum = 0;
    const visible = showAllPareto ? list : list.slice(0, Math.max(COLLAPSED_PARETO, vital));
    visible.forEach((p, i) => {
      cum += p.quantity;
      const row = make('div', 'prow' + (i < vital ? ' vital' : ''));
      const track = make('div', 'bartrack'); const bar = make('div', 'bar'); bar.style.width = `${Math.max(2, (p.quantity / max) * 100)}%`; track.append(bar);
      const name = make('button', 'barname rowbtn', p.phenomenonName); name.type = 'button';
      name.setAttribute('aria-label', `${p.phenomenonName}: ${fmt(p.quantity)} parts, ${pct(p.quantity / total, 0)} of rejected parts, cumulative ${pct(cum / total, 0)}. Show only this phenomenon.`);
      row.append(make('div', 'muted', String(i + 1)), name, track, make('div', 'num', fmt(p.quantity)),
        make('div', 'num', pct(p.quantity / total, 0)), make('div', 'num', pct(cum / total, 0)), make('div', 'num muted', shortDelta(p.quantity, p.previousQuantity)));
      hover(row, `${p.phenomenonName} (${p.shopCode}): ${fmt(p.quantity)} parts in ${fmt(p.entries)} entries, ${pct(p.quantity / total, 1)} of rejected parts; cumulative ${pct(cum / total, 1)}. Click to drill down.`, false);
      row.addEventListener('click', () => { drill.phenomenonId = String(p.phenomenonId); drill.phenomenonLabel = p.phenomenonName; load(true); });   // the button's click bubbles here
      host.append(row);
      if (i === vital - 1 && vital < list.length) host.append(make('div', 'pcut', `— ${pct(cut, 0)} of rejected parts above this line —`));
    });
    if (visible.length < list.length) {
      const more = make('button', 'link-dark', `Show all ${list.length} phenomena`); more.type = 'button';
      more.addEventListener('click', () => { showAllPareto = true; renderPareto(r); });
      host.append(more);
    }
    if (r.phenomenaCount > list.length) host.append(make('p', 'muted small', `${r.phenomenaCount - list.length} more phenomena with fewer parts are not listed.`));
  }

  function renderEntries(res) {
    lastEntries = res;
    const host = el('entries'); host.replaceChildren();
    if (res && res.error) { text(el('entriesNote'), res.error.message); return; }
    const all = res ? res.items : [];
    const items = showAllEntries ? all : all.slice(0, COLLAPSED_ENTRIES);
    text(el('entriesNote'), all.length === 0 ? '' : `${res.hasMore ? 'Newest ' + all.length + ' loaded (more exist: narrow the selection)' : all.length + ' entries'}` + (res.unavailableShops.length ? ` · not available: ${res.unavailableShops.join(', ')}` : ''));
    renderTable(host, ['Saved', 'Day / shift', 'Shop', 'Machine', 'Phenomenon', 'Qty', 'Inspector', 'Remarks'],
      items.map(e => ({ cells: [new Date(e.serverCreatedAtUtc).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }),
        `${e.productionDate ? shortDay(e.productionDate) : '–'} ${e.shiftCode ?? ''}`, e.shopCode, e.machineCode, e.phenomenonName, fmt(e.quantity),
        e.inspectorEmployeeCode, (e.correctsEntryId ? `[correction of #${e.correctsEntryId}] ` : '') + (e.remarks ?? '')] })),
      'No entries in this selection.', 5, 5);
    if (items.length < all.length) {
      const more = make('button', 'link-dark', `Show ${all.length - items.length} more`); more.type = 'button';
      more.addEventListener('click', () => { showAllEntries = true; renderEntries(lastEntries); });
      host.append(more);
    }
  }

  /** Rows: {cells, label?, onClick?}. Columns from numFrom to numTo are right-aligned numbers. */
  function renderTable(host, head, rows, empty, numFrom = 1, numTo = head.length - 1) {
    host.replaceChildren();
    if (!rows.length) { host.append(make('p', 'muted', empty)); return; }
    const isNum = i => i >= numFrom && i <= numTo;
    const t = make('table'); const tr = make('tr');
    head.forEach((h, i) => { const th = make('th', isNum(i) ? 'num' : '', h); th.scope = 'col'; tr.append(th); });
    const thead = make('thead'); thead.append(tr); t.append(thead);
    const tb = make('tbody');
    rows.forEach(r => {
      const row = make('tr', r.onClick ? 'clickable' : '');
      r.cells.forEach((c, i) => {
        const td = make(i === 0 ? 'th' : 'td', isNum(i) ? 'num' : '');
        if (i === 0) td.scope = 'row';
        // Clickable rows keep their table semantics; the first cell holds a real button for keyboard and screen readers.
        if (i === 0 && r.onClick) { const b = make('button', 'rowbtn', c); b.type = 'button'; if (r.label) b.title = r.label; td.append(b); }
        else td.textContent = c;
        row.append(td);
      });
      if (r.onClick) row.addEventListener('click', r.onClick);   // a click anywhere on the row (the button's click bubbles here)
      tb.append(row);
    });
    t.append(tb); host.append(t);
  }

  // ---- production counts upload (administrators) ----
  let parsedRows = null;
  function wireUpload() {
    el('prodTemplate').addEventListener('click', () => {
      const d = cal ? cal.productionDate : isoDay(new Date());
      const csv = `shop,machine,date,shift,produced\nGDC,GDC-01,${d},A,1200\nGDC,GDC-01,${d},B,1150\n`;
      const a = make('a'); a.href = URL.createObjectURL(new Blob([csv], { type: 'text/csv' })); a.download = 'production-counts-template.csv';
      document.body.append(a); a.click(); setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
    });
    el('prodFile').addEventListener('change', () => { parsedRows = null; el('prodApply').disabled = true; el('prodResult').replaceChildren(); });
    el('prodCheck').addEventListener('click', () => upload(true));
    el('prodApply').addEventListener('click', () => upload(false));
  }

  /** Splits one CSV line, honouring double quotes ("a,b" and "" inside quotes). */
  function csvFields(line, sep) {
    const out = []; let cur = '', q = false;
    for (let i = 0; i < line.length; i++) {
      const ch = line[i];
      if (q) { if (ch === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (ch === '"') q = false; else cur += ch; }
      else if (ch === '"') q = true; else if (ch === sep) { out.push(cur.trim()); cur = ''; } else cur += ch;
    }
    out.push(cur.trim());
    return out;
  }

  /** Returns rows with the FILE line number of each (blank lines are skipped but still counted). Throws listing bad lines. */
  function parseCsv(textIn) {
    const lines = textIn.replace(/^\uFEFF/, '').split(/\r?\n/).map((t, i) => ({ t, line: i + 1 })).filter(l => l.t.trim() !== '');
    if (!lines.length) throw new Error('The file is empty.');
    const sep = lines[0].t.includes(';') && !lines[0].t.includes(',') ? ';' : ',';
    const head = csvFields(lines[0].t, sep).map(h => h.toLowerCase());
    const idx = n => head.indexOf(n);
    for (const n of ['shop', 'machine', 'date', 'shift', 'produced']) if (idx(n) < 0) throw new Error(`Missing column "${n}". Expected: shop,machine,date,shift,produced`);
    const bad = [];
    const rows = lines.slice(1).map(({ t, line }) => {
      const c = csvFields(t, sep);
      const date = c[idx('date')] || '', producedText = c[idx('produced')] || '';
      if (!validDay(date)) bad.push(`Line ${line}: date must be a real day as YYYY-MM-DD (got "${date}").`);
      // Range check here too: an out-of-range number would otherwise fail as a whole-request error without a line.
      const produced = /^\d{1,7}$/.test(producedText) ? Number(producedText) : -1;
      if (produced < 0 || produced > 1000000) bad.push(`Line ${line}: produced must be a whole number from 0 to 1,000,000 (got "${producedText}").`);
      return { line, row: { shopCode: c[idx('shop')] || '', machineCode: c[idx('machine')] || '', productionDate: date, shiftCode: (c[idx('shift')] || '').toUpperCase(), producedQuantity: produced } };
    });
    if (!rows.length) throw new Error('The file has a header but no data rows.');
    if (bad.length) { const e = new Error(`Nothing was sent: ${bad.length} line(s) need fixing.`); e.lines = bad; throw e; }
    return rows;
  }

  let uploading = false;
  async function upload(dryRun) {
    if (uploading) return;   // no double submit
    const out = el('prodResult'); out.replaceChildren();
    uploading = true; el('prodCheck').disabled = el('prodApply').disabled = true;
    let applyEnabled = false;
    try {
      if (dryRun || !parsedRows) {
        const f = el('prodFile').files[0];
        if (!f) throw new Error('Choose a CSV file first.');
        if (f.size > 1_500_000) throw new Error('The file is too large (max about 5,000 rows).');
        parsedRows = parseCsv(await f.text());
      }
      const r = await api('/api/admin/production-counts', { method: 'POST', body: JSON.stringify({ rows: parsedRows.map(x => x.row), dryRun, source: 'upload' }) });
      if (r.problems.length) {
        // The server numbers data rows from 1; show the line of the FILE instead.
        problemList(out, `Nothing was saved: ${r.problems.length} row(s) need fixing.`,
          r.problems.map(p => `Line ${parsedRows[p.row - 1] ? parsedRows[p.row - 1].line : '?'}: ${p.problem}`));
        parsedRows = null;
        return;
      }
      out.append(make('p', dryRun ? 'readout' : 'ok', (dryRun ? 'Check passed: ' : 'Saved: ') +
        `${fmt(r.rows)} rows — ${fmt(r.inserted)} new, ${fmt(r.updated)} changed, ${fmt(r.unchanged)} unchanged.` + (dryRun ? ' Press "Save counts" to store them.' : '')));
      applyEnabled = dryRun;
      if (!dryRun) { parsedRows = null; el('prodFile').value = ''; load(); }
    } catch (e) {
      if (e.lines) problemList(out, e.message, e.lines); else out.append(make('p', 'error', e.message));
      parsedRows = null;
    } finally {
      uploading = false; el('prodCheck').disabled = false; el('prodApply').disabled = !applyEnabled;
    }
  }
  function problemList(out, title, lines) {
    out.append(make('p', 'error', title));
    const ul = make('ul', 'problems');
    lines.slice(0, 50).forEach(l => ul.append(make('li', '', l)));
    if (lines.length > 50) ul.append(make('li', 'muted', `… and ${lines.length - 50} more`));
    out.append(ul);
  }

  const slot = s => { const i = shifts.findIndex(x => x.shiftCode === s.shiftCode); return i >= 0 ? SHIFT_SLOTS[i % SHIFT_SLOTS.length] : '--series-other'; };
  /** Axis maximum = 4 equal whole-number steps of 1, 2 or 5 × 10^k (so ticks read 0, 10, 20, 30, 40, never 12.5). */
  function niceMax(v) {
    const raw = Math.max(1, v / 4), p = Math.pow(10, Math.floor(Math.log10(raw)));
    for (const m of [1, 2, 5, 10]) if (m * p >= raw && Number.isInteger(m * p)) return 4 * m * p;
    return 40 * p;
  }
  function barPath(x, y, w, h, r) {
    r = Math.min(r, w / 2, h);
    return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`;
  }
  function svgEl(tag, attrs) { const n = document.createElementNS('http://www.w3.org/2000/svg', tag); for (const k in attrs) n.setAttribute(k, attrs[k]); return n; }
  function hover(node, label, setAriaLabel = true) {
    const tip = el('tip');
    node.addEventListener('mousemove', ev => { tip.textContent = label; tip.hidden = false; tip.style.left = `${ev.clientX + 12}px`; tip.style.top = `${ev.clientY + 12}px`; });
    node.addEventListener('mouseleave', () => { tip.hidden = true; });
    if (setAriaLabel) node.setAttribute('aria-label', label);
  }
  void session;
})();
