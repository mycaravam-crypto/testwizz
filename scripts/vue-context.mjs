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
const propDetails = new Map();
const emitDetails = new Map();
const exposed = new Set();
const computed = new Set();
const watched = new Set();
const imports = new Set();
const mockCandidates = new Set();
const cleanName = name => name?.getText(ast).replace(/^['"]|['"]$/g, '') ?? '';
const collectNames = node => {
  if (!ts.isTypeLiteralNode(node)) return;
  for (const member of node.members) {
    if (!member.name) continue;
    const name = cleanName(member.name);
    if (!name) continue;
    props.add(name);
    const type = member.type?.getText(ast) ?? 'unknown';
    const optional = !!member.questionToken;
    propDetails.set(name, { name, type, required: !optional, defaultValue: null, validator: null });
  }
};
const collectEmitType = node => {
  if (!ts.isTypeLiteralNode(node)) return;
  for (const member of node.members) {
    if (!member.name) continue;
    const name = cleanName(member.name);
    if (!name) continue;
    emits.add(name);
    const parameters = ts.isMethodSignature(member) || ts.isCallSignatureDeclaration(member)
      ? member.parameters.map(parameter => `${parameter.name.getText(ast)}: ${parameter.type?.getText(ast) ?? 'unknown'}`)
      : [];
    const payload = parameters.length > 0 ? parameters.join(', ') : member.type?.getText(ast) ?? '';
    emitDetails.set(name, { name, payload });
  }
};
const visit = node => {
  if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)) {
    const call = node.expression.text;
    if (call === 'defineProps') {
      node.typeArguments?.forEach(collectNames);
      const obj = node.arguments[0];
      if (obj && ts.isObjectLiteralExpression(obj)) for (const entry of obj.properties) {
        if (!entry.name) continue;
        const name = cleanName(entry.name);
        if (!name) continue;
        props.add(name);
        const options = entry.initializer;
        let type = 'unknown';
        let required = false;
        let defaultValue = null;
        let validator = null;
        if (options && ts.isObjectLiteralExpression(options)) {
          for (const option of options.properties) {
            if (!option.name) continue;
            const key = cleanName(option.name);
            if (key === 'type') type = option.initializer?.getText(ast) ?? 'unknown';
            if (key === 'required' && option.initializer?.kind === ts.SyntaxKind.TrueKeyword) required = true;
            if (key === 'default') defaultValue = option.initializer?.getText(ast) ?? null;
            if (key === 'validator') validator = option.initializer?.getText(ast) ?? null;
          }
        } else if (options) {
          type = options.getText(ast);
        }
        propDetails.set(name, { name, type, required, defaultValue, validator });
      }
    }
    if (call === 'defineEmits') {
      node.typeArguments?.forEach(collectEmitType);
      const obj = node.arguments[0];
      if (obj && ts.isArrayLiteralExpression(obj)) for (const element of obj.elements) {
        if (ts.isStringLiteral(element)) emits.add(element.text);
      }
      if (obj && ts.isObjectLiteralExpression(obj)) for (const entry of obj.properties) {
        if (!entry.name) continue;
        const name = cleanName(entry.name);
        if (!name) continue;
        emits.add(name);
        emitDetails.set(name, { name, payload: entry.initializer?.getText(ast) ?? '' });
      }
    }
    if (call === 'defineExpose') {
      const obj = node.arguments[0];
      if (obj && ts.isObjectLiteralExpression(obj)) {
        for (const entry of obj.properties) if (entry.name) exposed.add(cleanName(entry.name));
      }
    }
    if (call === 'inject') mockCandidates.add('inject');
    if (call === 'useRouter' || call === 'useRoute' || call === 'useI18n') mockCandidates.add(call);
    if (call === 'fetch' || call === 'axios') mockCandidates.add(call);
    if (call === 'computed' || call === 'watch' || call === 'watchEffect') {
      const declaration = node.parent;
      if (ts.isVariableDeclaration(declaration) && ts.isIdentifier(declaration.name)) {
        (call === 'computed' ? computed : watched).add(declaration.name.text);
      }
    }
  }
  ts.forEachChild(node, visit);
};
for (const statement of ast.statements) {
  if (ts.isImportDeclaration(statement)) {
    const moduleName = statement.moduleSpecifier;
    if (ts.isStringLiteral(moduleName)) imports.add(moduleName.text);
  }
}
visit(ast);
if (descriptor.template) {
  // Vue's template AST exposes slot outlet names and named-slot declarations.
  const walk = node => {
    if (!node || typeof node !== 'object') return;
    if (node.type === 1 && node.tag === 'slot') {
      const name = node.props?.find(p => p.type === 6 && p.name === 'name');
      slots.add(name?.value?.content || 'default');
    }
    if (node.type === 1) {
      for (const prop of node.props ?? []) {
        if (prop.type === 7 && prop.name === 'slot' && prop.arg?.type === 4 && prop.arg.content) {
          slots.add(prop.arg.content);
        }
      }
    }
    if (Array.isArray(node.children)) node.children.forEach(walk);
    if (Array.isArray(node.branches)) node.branches.forEach(walk);
  };
  walk(descriptor.template.ast);
}
console.log(JSON.stringify({
  propDetails: [...propDetails.values()].sort((a, b) => a.name.localeCompare(b.name)),
  emitDetails: [...emitDetails.values()].sort((a, b) => a.name.localeCompare(b.name)),
  exposed: [...exposed].sort(),
  computed: [...computed].sort(),
  watched: [...watched].sort(),
  imports: [...imports].sort(),
  mockCandidates: [...mockCandidates].sort(),
  name: path.basename(filename, '.vue'),
  bindings: Object.keys(bindings).sort().map(name => ({ name, kind: bindings[name] })),
  template: !!descriptor.template,
  scriptSetup: !!descriptor.scriptSetup,
  styles: descriptor.styles.length,
  props: [...props].sort(),
  emits: [...emits].sort(),
  slots: [...slots].sort(),
}));
