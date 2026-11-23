import fs from 'node:fs';

// Shared DocWizz-compatible session loader for read-only snapshots and reviewed captures.
// State is held in memory; never written back, printed, or sent to the model.
export async function openContext(browser, target, options, storageFile) {
  if (!storageFile) return browser.newContext(options);
  const saved = JSON.parse(fs.readFileSync(storageFile, 'utf8'));
  const origins = (saved.origins ?? []).filter(entry => entry.origin === target.origin);
  const cookies = (saved.cookies ?? []).filter(cookie =>
    cookie.domain?.replace(/^\./, '').toLowerCase() === target.hostname.toLowerCase());
  const context = await browser.newContext({
    ...options,
    storageState: { cookies, origins: origins.map(({ origin, localStorage }) => ({ origin, localStorage })) }
  });
  const session = origins.flatMap(entry => entry.sessionStorage ?? []);
  if (session.length) {
    await context.addInitScript(({ origin, items }) => {
      if (location.origin !== origin) return;
      for (const { name, value } of items)
        if (sessionStorage.getItem(name) === null) sessionStorage.setItem(name, value);
    }, { origin: target.origin, items: session });
  }
  return context;
}

export function monitorAuthentication(page, target, storageFile) {
  if (!storageFile) return { check: async () => {} };
  let denied = false;
  let authorized = false;
  page.on('response', response => {
    const type = response.request().resourceType();
    if (type !== 'xhr' && type !== 'fetch') return;
    const status = response.status();
    if (status === 401 || status === 403) denied = true;
    if (status >= 200 && status < 300 &&
        response.request().headers()['authorization']) authorized = true;
  });
  return {
    check: async (navigation, initial = false) => {
      // Let the SPA finish its async auth guard and first API calls. Network-idle
      // is bounded, since polling applications may never become idle.
      try { await page.waitForLoadState('networkidle', { timeout: 5000 }); } catch {}
      await page.waitForTimeout(400);
      const landed = new URL(page.url());
      const status = navigation?.status();
      const wrongRoute = initial &&
        (landed.origin !== target.origin || landed.pathname !== target.pathname ||
         landed.hash !== target.hash);
      const passwordForm = await page.locator('input[type=password]:visible').count() > 0;
      if (status === 401 || status === 403 || denied || wrongRoute ||
          (passwordForm && !authorized))
        throw new Error('TESTWIZZ_AUTH_FAILED: session expired, inaccessible route or rejected API request');
    }
  };
}
