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
const propDefaults = new Map();
const emitDetails = new Map();
const exposed = new Set();
const computed = new Set();
const watched = new Set();
const imports = new Set();
const mockCandidates = new Set();
const cleanName = name => name?.getText(ast).replace(/^['"]|['"]$/g, '') ?? '';
const collectPropMembers = members => {
  for (const member of members) {
    if (!member.name) continue;
    const name = cleanName(member.name);
    if (!name) continue;
    props.add(name);
    const type = member.type?.getText(ast) ?? 'unknown';
    const optional = !!member.questionToken;
    propDetails.set(name, { name, type, required: !optional, defaultValue: null, validator: null });
  }
};
const collectNames = node => {
  if (ts.isTypeLiteralNode(node) || ts.isInterfaceDeclaration(node)) {
    collectPropMembers(node.members);
    return;
  }
  if (!ts.isTypeReferenceNode(node) || !ts.isIdentifier(node.typeName)) return;
  const declaration = ast.statements.find(statement =>
    (ts.isInterfaceDeclaration(statement) || ts.isTypeAliasDeclaration(statement)) &&
    statement.name.text === node.typeName.text);
  if (declaration && ts.isInterfaceDeclaration(declaration)) collectPropMembers(declaration.members);
  if (declaration && ts.isTypeAliasDeclaration(declaration)) collectNames(declaration.type);
};
const collectEmitType = node => {
  if (!ts.isTypeLiteralNode(node)) return;
  for (const member of node.members) {
    if (ts.isCallSignatureDeclaration(member)) {
      const [eventParameter, ...payloadParameters] = member.parameters;
      const eventType = eventParameter?.type;
      const name = eventType && ts.isLiteralTypeNode(eventType) && ts.isStringLiteral(eventType.literal)
        ? eventType.literal.text
        : '';
      if (!name) continue;
      emits.add(name);
      const payload = payloadParameters.map(parameter =>
        `${parameter.name.getText(ast)}: ${parameter.type?.getText(ast) ?? 'unknown'}`).join(', ');
      emitDetails.set(name, { name, payload });
      continue;
    }
    if (!member.name) continue;
    const name = cleanName(member.name);
    if (!name) continue;
    emits.add(name);
    const parameters = ts.isMethodSignature(member)
      ? member.parameters.map(parameter => `${parameter.name.getText(ast)}: ${parameter.type?.getText(ast) ?? 'unknown'}`)
      : [];
    const payload = parameters.length > 0 ? parameters.join(', ') : member.type?.getText(ast) ?? '';
    emitDetails.set(name, { name, payload });
  }
};
const visit = node => {
  if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)) {
    const call = node.expression.text;
    if (call === 'withDefaults') {
      const defaults = node.arguments[1];
      if (defaults && ts.isObjectLiteralExpression(defaults)) {
        for (const entry of defaults.properties) {
          if (!entry.name) continue;
          const name = cleanName(entry.name);
          propDefaults.set(name, entry.initializer?.getText(ast) ?? null);
        }
      }
    }
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
    if (call === 'useRouter' || call === 'useRoute' || call === 'useI18n' ||
        (call.startsWith('use') && call.endsWith('Store'))) mockCandidates.add(call);
    if (call === 'fetch' || call === 'axios' || call === 'useFetch') mockCandidates.add(call);
    if (ts.isPropertyAccessExpression(node.expression) &&
        ts.isIdentifier(node.expression.expression) && node.expression.expression.text === 'axios') {
      mockCandidates.add('axios');
    }
    if (call === 'computed' || call === 'watch' || call === 'watchEffect') {
      const declaration = node.parent;
      if (ts.isVariableDeclaration(declaration) && ts.isIdentifier(declaration.name)) {
        (call === 'computed' ? computed : watched).add(declaration.name.text);
      } else if (call === 'watch' || call === 'watchEffect') {
        watched.add(node.arguments[0]?.getText(ast) ?? call);
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
for (const [name, defaultValue] of propDefaults) {
  const existing = propDetails.get(name);
  if (existing) propDetails.set(name, { ...existing, defaultValue });
}
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

// Resolve project-local import aliases without executing vite.config or other project code.
const aliases = [];
const addAlias = (pattern, replacement, baseDirectory) => {
  if (!pattern || typeof replacement !== 'string') return;
  const target = path.isAbsolute(replacement)
    ? replacement
    : path.resolve(baseDirectory, replacement);
  aliases.push({ pattern, target });
};
for (const configName of ['tsconfig.json', 'jsconfig.json']) {
  const configPath = path.join(resolvedRoot, configName);
  if (!fs.existsSync(configPath)) continue;
  const read = ts.readConfigFile(configPath, ts.sys.readFile);
  if (read.error) continue;
  const parsedConfig = ts.parseJsonConfigFileContent(read.config, ts.sys, resolvedRoot, undefined, configPath);
  const baseDirectory = parsedConfig.options.baseUrl ?? resolvedRoot;
  for (const [pattern, targets] of Object.entries(parsedConfig.options.paths ?? {})) {
    for (const target of targets) addAlias(pattern, target, baseDirectory);
  }
}
const viteConfig = fs.readdirSync(resolvedRoot)
  .filter(name => /^vite\.config\.(?:[cm]?[jt]s)$/.test(name))
  .sort()[0];
if (viteConfig) {
  const configSource = fs.readFileSync(path.join(resolvedRoot, viteConfig), 'utf8');
  const configAst = ts.createSourceFile(viteConfig, configSource, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  const readString = node => node && (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node))
    ? node.text : null;
  const visitAliases = node => {
    if (ts.isPropertyAssignment(node) && node.name.getText(configAst).replace(/^['"]|['"]$/g, '') === 'alias') {
      if (ts.isObjectLiteralExpression(node.initializer)) {
        for (const entry of node.initializer.properties) {
          if (!ts.isPropertyAssignment(entry)) continue;
          const key = readString(entry.name) ?? entry.name.getText(configAst).replace(/^['"]|['"]$/g, '');
          const value = readString(entry.initializer);
          if (value !== null) addAlias(key, value, resolvedRoot);
        }
      } else if (ts.isArrayLiteralExpression(node.initializer)) {
        for (const entry of node.initializer.elements) {
          if (!ts.isObjectLiteralExpression(entry)) continue;
          const fields = new Map(entry.properties.filter(ts.isPropertyAssignment).map(p => [
            p.name.getText(configAst).replace(/^['"]|['"]$/g, ''), p.initializer
          ]));
          const key = readString(fields.get('find'));
          const value = readString(fields.get('replacement'));
          if (key && value !== null) addAlias(key, value, resolvedRoot);
        }
      }
    }
    ts.forEachChild(node, visitAliases);
  };
  visitAliases(configAst);
}
const resolveModule = specifier => {
  const alias = aliases
    .filter(item => specifier === item.pattern.replace(/\/\*$/, '') ||
      (item.pattern.includes('*') && specifier.startsWith(item.pattern.split('*')[0]) &&
       specifier.endsWith(item.pattern.split('*')[1] ?? '')) ||
      (!item.pattern.includes('*') && specifier.startsWith(item.pattern + '/')))
    .sort((a, b) => b.pattern.length - a.pattern.length)[0];
  if (!alias) return null;
  const wildcard = alias.pattern.indexOf('*');
  const suffix = wildcard >= 0 ? alias.pattern.slice(wildcard + 1) : '';
  const prefix = wildcard >= 0 ? alias.pattern.slice(0, wildcard) : alias.pattern;
  const remainder = wildcard >= 0
    ? specifier.slice(prefix.length, specifier.length - suffix.length)
    : specifier === alias.pattern ? '' : specifier.slice(alias.pattern.length + 1);
  const base = wildcard >= 0
    ? alias.target.replace(/\*$/, '') + remainder
    : path.join(alias.target, remainder);
  const candidates = [base, ...['.ts', '.tsx', '.js', '.jsx', '.vue', '.d.ts'].map(ext => base + ext),
    ...['index.ts', 'index.tsx', 'index.js', 'index.vue'].map(name => path.join(base, name))];
  for (const candidate of candidates) {
    if (!fs.existsSync(candidate)) continue;
    try {
      if (fs.statSync(candidate).isFile()) return path.relative(resolvedRoot, candidate).split(path.sep).join('/');
    } catch { /* Ignore inaccessible candidates; resolution remains unavailable. */ }
  }
  return null;
};
const resolvedImports = [...imports].sort().map(specifier => ({
  specifier,
  path: resolveModule(specifier),
  kind: resolveModule(specifier) ? 'alias' : 'unresolved-or-external'
}));
const ignoredDirectories = new Set(['node_modules', 'dist', 'build', '.git', 'coverage']);
const projectFiles = [];
const walkFiles = directory => {
  let entries;
  try { entries = fs.readdirSync(directory, { withFileTypes: true }); } catch { return; }
  for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
    if (entry.isDirectory()) {
      if (!ignoredDirectories.has(entry.name)) walkFiles(path.join(directory, entry.name));
    } else if (entry.isFile()) {
      projectFiles.push(path.join(directory, entry.name));
    }
  }
};
walkFiles(resolvedRoot);
const targetBase = path.basename(filename, path.extname(filename));
const testFiles = projectFiles.filter(file => /\.(?:spec|test)\.(?:ts|tsx|js|jsx|mjs|cjs)$/.test(file));
const relatedTests = [];
for (const testFile of testFiles) {
  const relativeTest = path.relative(resolvedRoot, testFile).split(path.sep).join('/');
  let testSource;
  try { testSource = fs.readFileSync(testFile, 'utf8'); } catch { continue; }
  const baseName = path.basename(testFile);
  let matchedOn = null;
  let line = 1;
  if (baseName.startsWith(targetBase + '.')) {
    matchedOn = 'basename';
  } else {
    const candidates = [relative, ...resolvedImports.filter(item => item.path).map(item => item.path)];
    const match = candidates.find(candidate => testSource.includes(candidate) ||
      testSource.includes(candidate.replace(/\.(?:vue|tsx?|jsx?)$/, '')));
    if (match) {
      matchedOn = 'import';
      const offset = testSource.indexOf(match);
      line = testSource.slice(0, offset).split('\n').length;
    }
  }
  if (matchedOn) relatedTests.push({ name: baseName, file: relativeTest, line, matchedOn });
}
relatedTests.sort((a, b) => a.file.localeCompare(b.file));

console.log(JSON.stringify({
  propDetails: [...propDetails.values()].sort((a, b) => a.name.localeCompare(b.name)),
  emitDetails: [...emitDetails.values()].sort((a, b) => a.name.localeCompare(b.name)),
  exposed: [...exposed].sort(),
  computed: [...computed].sort(),
  watched: [...watched].sort(),
  imports: [...imports].sort(),
  mockCandidates: [...mockCandidates].sort(),
  resolvedImports,
  relatedTests,
  name: path.basename(filename, '.vue'),
  bindings: Object.keys(bindings).sort().map(name => ({ name, kind: bindings[name] })),
  template: !!descriptor.template,
  scriptSetup: !!descriptor.scriptSetup,
  styles: descriptor.styles.length,
  props: [...props].sort(),
  emits: [...emits].sort(),
  slots: [...slots].sort(),
}));
