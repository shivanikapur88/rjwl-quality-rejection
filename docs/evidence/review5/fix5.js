// Fifth review: browser checks of the dashboard / desktop-app fixes against the demo API.
const { chromium } = require('playwright');
(async () => {
  const out = process.argv[2]; const B = 'http://127.0.0.1:5080/dashboard/';
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage(); const errs = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text()); });
  await page.goto(B);
  const userSel = (await page.$('#username')) ? '#username' : ((await page.$('#user')) ? '#user' : 'input[type=text]');
  await page.fill(userSel, 'anita.s'); await page.fill('input[type=password]', 'Demo-Pass-12345');
  await page.click('form button[type=submit]');
  await page.waitForSelector('#nav:not([hidden])', { timeout: 15000 });
  await page.waitForTimeout(2000);
  const res = {};
  // 1. Badge text has a space before "vs".
  res.badgeText = await page.evaluate(() => { const t = [...document.querySelectorAll('.badge')][0]; return t ? t.parentElement.textContent : null; });
  res.badgeSpaced = /(fewer|more|0%) vs /.test(res.badgeText || '');
  // 2. Long range: last two axis labels at least 48 px apart.
  await page.goto(B + '#from=2026-09-01&to=2026-11-28'); await page.waitForTimeout(2500);
  res.axis = await page.evaluate(() => { const xs = [...document.querySelectorAll('svg text.axis')].filter(t => /[A-Z][a-z]{2} \d/.test(t.textContent)).map(t => +t.getAttribute('x')); return xs.slice(-2); });
  res.axisGapOk = res.axis.length === 2 && res.axis[1] - res.axis[0] >= 48;
  await page.screenshot({ path: out + '/fix5-axis-1440.png' });
  // 3. Focus after "Show all N phenomena" / "Show N more".
  for (const [label, re] of [['pareto', /^Show all \d+ phenomena$/], ['entries', /^Show \d+ more$/]]) {
    const btn = page.getByRole('button', { name: re });
    if (await btn.count()) { await btn.first().focus(); await page.keyboard.press('Enter'); await page.waitForTimeout(300);
      res['focus_' + label] = await page.evaluate(() => { const a = document.activeElement; return a ? a.tagName + '.' + a.className + ' ' + (a.textContent || '').slice(0, 30) : null; }); }
    else res['focus_' + label] = 'button not present';
  }
  // 4. Drill into a machine, then pick a range where it has nothing: the chip keeps the code.
  await page.goto(B + '#from=2026-11-22&to=2026-11-28'); await page.waitForTimeout(2000);
  const mBtn = page.locator('#machines button.rowbtn').first();
  const mCode = (await mBtn.textContent()).trim(); await mBtn.click(); await page.waitForTimeout(1500);
  await page.fill('#from', '2026-01-01').catch(() => {}); await page.fill('#to', '2026-01-02').catch(() => {});
  await page.click('#show').catch(async () => page.getByRole('button', { name: 'Show', exact: true }).click());
  await page.waitForTimeout(1500);
  res.chip = await page.evaluate(() => [...document.querySelectorAll('#chips .chip')].map(c => c.textContent));
  res.chipKeepsCode = res.chip.some(c => c.includes(mCode));
  // 5. Service worker: a 5xx for a dashboard file falls back to the saved copy; query strings share one saved copy.
  await page.goto(B); await page.waitForTimeout(1500); await page.reload(); await page.waitForTimeout(1500);
  await ctx.route('**/dashboard/labels.html*', r => r.fulfill({ status: 503, body: 'down' }));
  res.sw503 = await page.evaluate(async () => { const r = await fetch('labels.html?x=1'); const t = await r.text(); return { status: r.status, savedCopy: t.includes('<html') || t.includes('<!doctype') }; });
  await ctx.unroute('**/dashboard/labels.html*');
  await page.evaluate(() => fetch('index.html?a=1')); await page.waitForTimeout(500);
  res.cacheKeys = await page.evaluate(async () => { const all = []; for (const k of await caches.keys()) { const c = await caches.open(k); for (const r of await c.keys()) all.push(k + ' ' + new URL(r.url).pathname + new URL(r.url).search); } return all; });
  res.noQueryKeys = res.cacheKeys.every(k => !k.includes('?'));
  res.apiCached = res.cacheKeys.filter(k => k.includes('/api/')).length;
  res.consoleErrors = errs;
  console.log(JSON.stringify(res, null, 2));
  await browser.close();
})();
