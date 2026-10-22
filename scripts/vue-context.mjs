import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const [root, relative] = process.argv.slice(2);
if (!root || !relative || path.isAbsolute(relative) || relative.split(/[\\/]/).includes('..')) {
  console.error('Invalid project-relative Vue target.');
  process.exit(2);
}
const resolvedRoot = fs.realpathSync(root);
const filename = path.resolve(resolvedRoot, relative);
if (!filename.startsWith(resolvedRoot + path.sep) || !filename.endsWith('.vue') || !fs.statSync(filename).isFile() || !fs.realpathSync(filename).startsWith(resolvedRoot + path.sep)) {
  console.error('Vue target must be an existing .vue file within the project.');
  process.exit(2);
}
const require = createRequire(path.join(resolvedRoot, 'package.json'));
let compiler;
try {
  compiler = require('@vue/compiler-sfc');
  require('typescript');
} catch {
  console.error('Install project dependencies: npm install (requires @vue/compiler-sfc and typescript in the target project).');
  process.exit(2);
}
const source = fs.readFileSync(filename, 'utf8');
const parsed = compiler.parse(source, { filename });
if (parsed.errors.length) {
  console.error('Vue SFC parse failed: ' + parsed.errors.map(String).join('; '));
  process.exit(2);
}
const descriptor = parsed.descriptor;
let bindings = {};
try {
  if (descriptor.scriptSetup) {
    const script = compiler.compileScript(descriptor, { id: 'testwizz-context' });
    bindings = script.bindings || {};
  }
} catch (e) {
  console.error('Vue script compilation failed: ' + String(e.message || e));
  process.exit(2);
}
console.log(JSON.stringify({
  name: path.basename(filename, '.vue'),
  bindings: Object.keys(bindings).sort().map(name => ({ name, kind: bindings[name] })),
  template: !!descriptor.template,
  scriptSetup: !!descriptor.scriptSetup,
  styles: descriptor.styles.length,
}));
