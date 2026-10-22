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
let ts;
try {
  compiler = require('@vue/compiler-sfc');
  ts = require('typescript');
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
// AST extraction, not regex matching: only declarations and template slots are reported.
const scriptText = descriptor.scriptSetup?.content ?? descriptor.script?.content ?? '';
const ast = ts.createSourceFile(filename + '.ts', scriptText, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
const props = new Set();
const emits = new Set();
const slots = new Set();
const collectNames = node => {
  if (ts.isTypeLiteralNode(node)) {
    for (const member of node.members) if (member.name) {
      const name = member.name.getText(ast).replace(/^['"]|['"]$/g, '');
      if (name) props.add(name);
    }
  }
};
const collectEmitType = node => {
  if (ts.isTypeLiteralNode(node)) {
    for (const member of node.members) if (member.name) emits.add(member.name.getText(ast).replace(/^['"]|['"]$/g, ''));
  }
};
const visit = node => {
  if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)) {
    const call = node.expression.text;
    if (call === 'defineProps') {
      node.typeArguments?.forEach(collectNames);
      const obj = node.arguments[0];
      if (obj && ts.isObjectLiteralExpression(obj)) for (const entry of obj.properties) {
        if (entry.name) props.add(entry.name.getText(ast).replace(/^['"]|['"]$/g, ''));
      }
    }
    if (call === 'defineEmits') {
      node.typeArguments?.forEach(collectEmitType);
      const obj = node.arguments[0];
      if (obj && ts.isArrayLiteralExpression(obj)) for (const element of obj.elements) {
        if (ts.isStringLiteral(element)) emits.add(element.text);
      }
    }
  }
  ts.forEachChild(node, visit);
};
visit(ast);
if (descriptor.template) {
  // Vue's template AST exposes slot outlet names and named-slot declarations.
  const walk = node => {
    if (!node || typeof node !== 'object') return;
    if (node.type === 1 && node.tag === 'slot') {
      const name = node.props?.find(p => p.type === 6 && p.name === 'name');
      slots.add(name?.value?.content || 'default');
    }
    if (Array.isArray(node.children)) node.children.forEach(walk);
    if (Array.isArray(node.branches)) node.branches.forEach(walk);
  };
  walk(descriptor.template.ast);
}
console.log(JSON.stringify({
  name: path.basename(filename, '.vue'),
  bindings: Object.keys(bindings).sort().map(name => ({ name, kind: bindings[name] })),
  template: !!descriptor.template,
  scriptSetup: !!descriptor.scriptSetup,
  styles: descriptor.styles.length,
  props: [...props].sort(),
  emits: [...emits].sort(),
  slots: [...slots].sort(),
}));
