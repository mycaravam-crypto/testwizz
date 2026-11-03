import { createRequire } from 'node:module';
import path from 'node:path';

// A trusted, read-only browser probe. The AI never supplies executable code here.
const [root, address] = process.argv.slice(2);
const target = new URL(address);
const require = createRequire(path.join(root, 'package.json'));
const { chromium } = require('@playwright/test');

const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({ serviceWorkers: 'block' });
  await context.route('**/*', route => {
    const request = new URL(route.request().url());
    return request.origin === target.origin ? route.continue() : route.abort();
  });

  const page = await context.newPage();
  await page.goto(address, { waitUntil: 'domcontentloaded', timeout: 12000 });
  await page.waitForTimeout(300);
  const snapshot = await page.locator('body').ariaSnapshot({ timeout: 5000 });
  console.log(JSON.stringify({
    title: (await page.title()).slice(0, 200),
    snapshot: snapshot.slice(0, 14000)
  }));
} finally {
  await browser.close();
}
