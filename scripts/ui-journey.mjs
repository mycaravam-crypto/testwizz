import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

// Trusted, bounded interpreter for explicitly reviewed actions, not model-generated JavaScript.
const [root, address, file, output] = process.argv.slice(2);
const target = new URL(address);
if (target.protocol !== 'http:' || !['localhost', '127.0.0.1', '[::1]'].includes(target.hostname))
  throw Error('Only local test applications are supported');
const { chromium, expect } = createRequire(path.join(root, 'package.json'))('@playwright/test');
const definition = JSON.parse(fs.readFileSync(file, 'utf8'));
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 }, serviceWorkers: 'block'
  });
  await context.routeWebSocket('**/*', socket => socket.close());
  await context.route('**/*', route => {
    const request = new URL(route.request().url());
    // Actions may mutate local test data, but requests may not leave the local origin.
    return request.origin === target.origin ? route.continue() : route.abort();
  });
  const page = await context.newPage();
  await page.goto(address, { waitUntil: 'domcontentloaded', timeout: 12000 });
  const ids = [];
  for (const step of definition.steps) {
    const locator = step.role ? page.getByRole(step.role, { name: step.name, exact: true })
      : step.label ? page.getByLabel(step.label, { exact: true })
      : page.getByTestId(step.testId);
    if (step.action === 'click') await locator.click({ timeout: 7000 });
    else if (step.action === 'fill') await locator.fill(step.value, { timeout: 7000 });
    else throw Error('Unsupported step');
    if (new URL(page.url()).origin !== target.origin) throw Error('Navigation left the local origin');
    if (step.expectRole)
      await expect(page.getByRole(step.expectRole, { name: step.expectName, exact: true })).toBeVisible({ timeout: 7000 });
    else
      await expect(page.getByText(step.expectText, { exact: true })).toBeVisible({ timeout: 7000 });
    const image = path.join(output, 'screenshots', 'step-' + String(step.stepId).padStart(4, '0') + '.png');
    fs.mkdirSync(path.dirname(image), { recursive: true });
    await page.screenshot({ path: image, animations: 'disabled', fullPage: false });
    ids.push(step.stepId);
  }
  console.log(JSON.stringify({ stepIds: ids }));
} finally {
  await browser.close();
}
