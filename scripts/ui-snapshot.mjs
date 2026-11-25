import { createRequire } from 'node:module';
import path from 'node:path';
import { openContext, monitorAuthentication } from './ui-auth.mjs';

// A trusted, read-only browser probe. The AI never supplies executable code here.
const [root, address, storageFile] = process.argv.slice(2);
const target = new URL(address);
const require = createRequire(path.join(root, 'package.json'));
const { chromium } = require('@playwright/test');

const browser = await chromium.launch({ headless: true });
try {
  const context = await openContext(browser, target, { serviceWorkers: 'block' }, storageFile);
  // Browser observation is non-interactive: do not allow background WebSocket traffic.
  await context.routeWebSocket('**/*', socket => socket.close());
  await context.route('**/*', route => {
    const request = new URL(route.request().url());
    const readOnly = ['GET', 'HEAD'].includes(route.request().method());
    return request.origin === target.origin && readOnly ? route.continue() : route.abort();
  });

  const page = await context.newPage();
  const auth = monitorAuthentication(page, target, storageFile);
  const navigation = await page.goto(address, { waitUntil: 'domcontentloaded', timeout: 12000 });
  await auth.check(navigation, true);
  await page.waitForTimeout(300);
  // ariaSnapshot includes the current value of every textbox, password fields included (issue #11). Values of
  // credential and payment fields are cleared in this disposable page before the snapshot, so they never reach the
  // model; their labels stay. Programmatic value changes fire no input events, and no request can leave the origin.
  await page.evaluate(() => {
    const sensitive = /(^|\s)(current-password|new-password|one-time-code|cc-number|cc-csc|cc-exp(-month|-year)?)(\s|$)/;
    for (const input of document.querySelectorAll('input')) {
      const type = (input.getAttribute('type') || '').toLowerCase();
      if (type === 'password' || sensitive.test((input.getAttribute('autocomplete') || '').toLowerCase())) input.value = '';
    }
  });
  const snapshot = await page.locator('body').ariaSnapshot({ timeout: 5000 });
  console.log(JSON.stringify({
    title: (await page.title()).slice(0, 200),
    snapshot: snapshot.slice(0, 14000)
  }));
} finally {
  await browser.close();
}
