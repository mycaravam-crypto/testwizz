// Regression for issue #11: which sensitive values of a synthetic page reach the UI snapshot sent to the model.
// Runs the real scripts/ui-snapshot.mjs against a local page in a real browser.
// TESTWIZZ_PLAYWRIGHT_ROOT may point at another directory that provides @playwright/test (default: this fixture).
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const execute = promisify(execFile);
const fixture = dirname(fileURLToPath(import.meta.url));
const script = resolve(fixture, '../../scripts/ui-snapshot.mjs');
const root = process.env.TESTWIZZ_PLAYWRIGHT_ROOT || fixture;

const page = `<!doctype html><html><head><title>Account settings</title></head><body>
<h1>Account settings</h1>
<form>
  <label for="pw">Password</label><input id="pw" type="password" value="PW-PASSWORD-SECRET">
  <label for="cur">Current password</label><input id="cur" type="text" autocomplete="current-password" value="AUTOCOMPLETE-PASSWORD-SECRET">
  <label for="otp">One-time code</label><input id="otp" autocomplete="one-time-code" value="OTP-SECRET">
  <label for="card">Card number</label><input id="card" autocomplete="cc-number" value="CARD-SECRET">
  <input type="hidden" name="csrf" value="CSRF-HIDDEN-SECRET">
  <label for="city">City</label><input id="city" value="Hamburg">
</form>
<div style="display:none">DISPLAYNONE-SECRET</div>
<div aria-hidden="true">ARIAHIDDEN-SECRET</div>
<div data-token="DATAATTR-SECRET">Profile</div>
</body></html>`;

const server = createServer((request, response) => {
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  response.end(page);
});
await new Promise(done => server.listen(0, '127.0.0.1', done));
try {
  const { stdout } = await execute(process.execPath, [script, root, `http://127.0.0.1:${server.address().port}/`]);
  const { snapshot } = JSON.parse(stdout);

  // Credential and payment field values are cleared; their labels remain so tests can still address the fields.
  for (const secret of ['PW-PASSWORD-SECRET', 'AUTOCOMPLETE-PASSWORD-SECRET', 'OTP-SECRET', 'CARD-SECRET']) {
    assert.ok(!snapshot.includes(secret), `${secret} leaked into the snapshot:\n${snapshot}`);
  }
  assert.match(snapshot, /textbox "Password"/);
  assert.match(snapshot, /textbox "Card number"/);
  // Never part of the accessibility tree in the first place.
  for (const secret of ['CSRF-HIDDEN-SECRET', 'DISPLAYNONE-SECRET', 'ARIAHIDDEN-SECRET', 'DATAATTR-SECRET']) {
    assert.ok(!snapshot.includes(secret), `${secret} leaked into the snapshot:\n${snapshot}`);
  }
  // Ordinary field values stay visible to the model; that is the documented trust boundary (sanitized test data).
  assert.match(snapshot, /textbox "City": Hamburg/);
  console.log('UI snapshot redaction: ok');
} finally {
  server.close();
}
