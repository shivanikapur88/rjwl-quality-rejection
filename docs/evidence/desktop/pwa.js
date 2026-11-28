const { chromium } = require('playwright');
(async () => {
  const out = process.argv[2]; const B = 'http://127.0.0.1:5080/dashboard/';
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 2 });
  const page = await ctx.newPage(); const errs = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text()); });
  await page.goto(B);
  const cdp = await ctx.newCDPSession(page);
  const userSel = (await page.$('#username')) ? '#username' : ((await page.$('#user')) ? '#user' : 'input[type=text]');
  await page.fill(userSel, 'anita.s'); await page.fill('input[type=password]', 'Demo-Pass-12345');
  await page.click('form button[type=submit]');
  await page.waitForSelector('#nav:not([hidden])', { timeout: 15000 });
  await page.waitForTimeout(2500);
  const sw = await page.evaluate(async () => { const r = await navigator.serviceWorker.ready; return { scope: r.scope, active: !!r.active }; });
  await page.reload(); await page.waitForTimeout(2500);
  const controlled = await page.evaluate(() => !!navigator.serviceWorker.controller);
  const manifest = await cdp.send('Page.getAppManifest');
  const inst = await cdp.send('Page.getInstallabilityErrors');
  const cached = await page.evaluate(async () => {
    const all = [];
    for (const k of await caches.keys()) { const c = await caches.open(k); for (const r of await c.keys()) all.push(new URL(r.url).pathname); }
    return all;
  });
  // Headless Chromium does not fire beforeinstallprompt; simulate it to show the button as Edge/Chrome would.
  await page.evaluate(() => { const e = new Event('beforeinstallprompt'); e.prompt = () => {}; e.userChoice = Promise.resolve({}); window.dispatchEvent(e); });
  await page.waitForTimeout(800);
  const installVisible = await page.isVisible('#install');
  await page.screenshot({ path: out + '/desktop-install-button.png' });
  console.log(JSON.stringify({ sw, controlled, manifestUrl: manifest.url, manifestErrors: manifest.errors,
    installabilityErrors: inst.installabilityErrors, cachedPaths: cached, apiCached: cached.filter(p => p.startsWith('/api/')),
    installVisible, consoleErrors: errs }, null, 2));
  await browser.close();
})();
