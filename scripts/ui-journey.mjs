import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

// Reviewed locators only. The browser never evaluates AI-generated code.
const [root, address, file, output] = process.argv.slice(2);
const target = new URL(address);
if (target.protocol !== 'http:' || !['localhost', '127.0.0.1', '[::1]'].includes(target.hostname))
  throw Error('Only local test applications are supported');
const { chromium, expect } = createRequire(path.join(root, 'package.json'))('@playwright/test');
const definition = JSON.parse(fs.readFileSync(file, 'utf8'));
const browser = await chromium.launch({ headless: true });

function actionLocator(page, step) {
  return step.role ? page.getByRole(step.role, { name: step.name, exact: true })
    : step.label ? page.getByLabel(step.label, { exact: true })
    : page.getByTestId(step.testId);
}

async function visibleCallouts(page, start) {
  const viewport = page.viewportSize();
  const markers = [];
  // The current action takes priority, but any other reviewed action still visible
  // on the same screen is included, even if it precedes the current step.
  const order = [start, ...definition.steps.map((_, i) => i).filter(i => i !== start)];
  for (const i of order) {
    const step = definition.steps[i];
    const locator = actionLocator(page, step);
    if (await locator.count() !== 1) {
      if (i === start) throw Error('Current action locator must identify exactly one element');
      continue;
    }
    const box = await locator.boundingBox();
    if (!box || box.width <= 0 || box.height <= 0 ||
        box.x + box.width <= 0 || box.x >= viewport.width ||
        box.y + box.height <= 0 || box.y >= viewport.height) {
      if (i === start) throw Error('Current action locator is not visible');
      continue;
    }
    // The guide number is the journey position, not the ADO step ID.
    // Keep the marker outside the control, clamped to the viewport.
    const right = box.x + box.width + 20;
    const left = box.x - 20;
    const x = Math.round(Math.max(18, Math.min(viewport.width - 18,
      right <= viewport.width - 18 ? right : left)));
    const y = Math.round(Math.max(18, Math.min(viewport.height - 18,
      box.y + Math.min(22, box.height / 2))));
    // Avoid superimposing multiple numbered circles on an identical target.
    if (markers.some(m => Math.abs(m.x - x) < 24 && Math.abs(m.y - y) < 24)) {
      if (i === start) throw Error('Current action locator overlaps an existing marker');
      continue;
    }
    markers.push({ number: i + 1, stepId: step.stepId,
      target: step.label ?? step.name ?? step.testId, x, y });
  }
  return markers.sort((a, b) => a.number - b.number);
}

async function captureAnnotated(page, file, markers) {
  // Fixed overlay is independent of application CSS and is removed before clicking.
  await page.evaluate(items => {
    const overlay = document.createElement('div');
    overlay.id = '__testwizz_doc_callouts__';
    overlay.setAttribute('aria-hidden', 'true');
    Object.assign(overlay.style, { position: 'fixed', inset: '0',
      zIndex: '2147483647', pointerEvents: 'none' });
    for (const marker of items) {
      const bubble = document.createElement('span');
      bubble.textContent = String(marker.number);
      Object.assign(bubble.style, {
        position: 'absolute', left: marker.x + 'px', top: marker.y + 'px',
        transform: 'translate(-50%, -50%)', boxSizing: 'border-box',
        width: '32px', height: '32px', borderRadius: '50%',
        background: '#1458a5', border: '2px solid white', color: '#fff',
        font: 'bold 16px/28px system-ui, sans-serif', textAlign: 'center',
        boxShadow: '0 2px 5px rgba(0,0,0,.45)'
      });
      overlay.appendChild(bubble);
    }
    document.documentElement.appendChild(overlay);
  }, markers);
  try {
    await page.screenshot({ path: file, animations: 'disabled', fullPage: false,
      scale: 'css' });
  } finally {
    await page.evaluate(() => document.getElementById('__testwizz_doc_callouts__')?.remove());
  }
}

try {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 }, serviceWorkers: 'block'
  });
  await context.routeWebSocket('**/*', socket => socket.close());
  await context.route('**/*', route => {
    const request = new URL(route.request().url());
    return request.origin === target.origin ? route.continue() : route.abort();
  });
  const page = await context.newPage();
  await page.goto(address, { waitUntil: 'domcontentloaded', timeout: 12000 });
  const ids = [];
  const guides = [];
  for (let i = 0; i < definition.steps.length; i++) {
    const step = definition.steps[i];
    const locator = actionLocator(page, step);
    await locator.scrollIntoViewIfNeeded({ timeout: 7000 });
    const markers = await visibleCallouts(page, i);
    const folder = path.join(output, 'screenshots');
    fs.mkdirSync(folder, { recursive: true });
    const name = 'step-' + String(step.stepId).padStart(4, '0');
    const annotated = path.join(folder, name + '-guide.png');
    await captureAnnotated(page, annotated, markers);
    guides.push({ stepId: step.stepId, callouts: markers });

    if (step.action === 'click') await locator.click({ timeout: 7000 });
    else if (step.action === 'fill') await locator.fill(step.value, { timeout: 7000 });
    else throw Error('Unsupported step');
    if (new URL(page.url()).origin !== target.origin) throw Error('Navigation left the local origin');
    if (step.expectRole)
      await expect(page.getByRole(step.expectRole, { name: step.expectName, exact: true })).toBeVisible({ timeout: 7000 });
    else
      await expect(page.getByText(step.expectText, { exact: true })).toBeVisible({ timeout: 7000 });
    await page.screenshot({ path: path.join(folder, name + '.png'),
      animations: 'disabled', fullPage: false, scale: 'css' });
    ids.push(step.stepId);
  }
  console.log(JSON.stringify({ stepIds: ids, guides }));
} finally {
  await browser.close();
}
