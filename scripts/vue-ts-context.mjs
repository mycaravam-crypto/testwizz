import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const [rootArg, relative] = process.argv.slice(2);
if (!rootArg || !relative || path.isAbsolute(relative) || relative.split(/[\\/]/).includes('..') ||
    !/\.tsx?$/.test(relative) || !/(^|[\\/])(composables|stores)[\\/]/.test(relative)) {
  console.error('Target must be a project-relative .ts/.tsx file under src/composables or src/stores.');
  process.exit(2);
}
const root = fs.realpathSync(rootArg);
const filename = path.resolve(root, relative);
if (!filename.startsWith(root + path.sep) || !fs.existsSync(filename) ||
    !fs.statSync(filename).isFile() || !fs.realpathSync(filename).startsWith(root + path.sep)) {
  console.error('TypeScript target must be an existing file within the project.');
  process.exit(2);
}
const require = createRequire(path.join(root, 'package.json'));
let ts;
try { ts = require('typescript'); }
catch {
  console.error('Install project dependencies: npm install (requires typescript in the target project).');
  process.exit(2);
}
const source = fs.readFileSync(filename, 'utf8');
const ast = ts.createSourceFile(filename, source, ts.ScriptTarget.Latest, true,
  filename.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
if (ast.parseDiagnostics.length) {
  console.error('TypeScript parse failed: ' + ast.parseDiagnostics.map(d => ts.flattenDiagnosticMessageText(d.messageText, '\\n')).join('; '));
  process.exit(2);
}
const textOf = node => node?.getText(ast) ?? '';
const lineOf = node => ast.getLineAndCharacterOfPosition(node.getStart(ast)).line + 1;
const nameOf = node => {
  const value = node && (ts.isIdentifier(node) || ts.isStringLiteral(node)) ? node : node?.name;
  return value && (ts.isIdentifier(value) || ts.isStringLiteral(value)) ? value.text : '';
};
const members = [];
const imports = [];
const dependencies = new Set();
const add = (id, signature, node) => members.push({ id, signature, line: lineOf(node) });
const isExported = node => !!node.modifiers?.some(m => m.kind === ts.SyntaxKind.ExportKeyword);
const unwrap = node => node && (ts.isAsExpression(node) || ts.isSatisfiesExpression(node) ||
  ts.isParenthesizedExpression(node)) ? unwrap(node.expression) : node;
const objectProperties = node => {
  node = unwrap(node);
  if (node && (ts.isArrowFunction(node) || ts.isFunctionExpression(node))) {
    if (ts.isBlock(node.body)) node = node.body.statements.filter(ts.isReturnStatement).at(-1)?.expression;
    else node = node.body;
    node = unwrap(node);
  }
  return node && ts.isObjectLiteralExpression(node) ? node.properties : [];
};
const collectSetupStore = (callback, storeName) => {
  const body = callback && (ts.isArrowFunction(callback) || ts.isFunctionExpression(callback)) ? callback.body : null;
  if (!body || !ts.isBlock(body)) return;
  const returned = body.statements.filter(ts.isReturnStatement).at(-1)?.expression;
  const localActions = new Set();
  const localGetters = new Set();
  for (const statement of body.statements) {
    if (ts.isFunctionDeclaration(statement) && statement.name) localActions.add(statement.name.text);
    if (ts.isVariableStatement(statement)) {
      for (const declaration of statement.declarationList.declarations) {
        const init = unwrap(declaration.initializer);
        if (nameOf(declaration) && init && (ts.isArrowFunction(init) || ts.isFunctionExpression(init)))
          localActions.add(nameOf(declaration));
        if (nameOf(declaration) && init && ts.isCallExpression(init) &&
            ts.isIdentifier(init.expression) && init.expression.text === 'computed')
          localGetters.add(nameOf(declaration));
      }
    }
  }
  for (const property of objectProperties(returned)) {
    const name = nameOf(property.name);
    if (!name) continue;
    if (ts.isMethodDeclaration(property) || (ts.isPropertyAssignment(property) &&
        (ts.isArrowFunction(property.initializer) || ts.isFunctionExpression(property.initializer))) ||
        (ts.isShorthandPropertyAssignment(property) && localActions.has(name))) {
      add('store-action:' + name, 'action ' + name + ' ' + textOf(property), property);
    } else {
      const init = ts.isPropertyAssignment(property) ? unwrap(property.initializer) : null;
      const role = (init && ts.isCallExpression(init) && ts.isIdentifier(init.expression) &&
        init.expression.text === 'computed') ||
        (ts.isShorthandPropertyAssignment(property) && localGetters.has(name)) ? 'getter' : 'state';
      add('store-' + role + ':' + name, role + ' ' + name + ': ' + textOf(property), property);
    }
  }
};
const inspectStore = node => {
  const call = unwrap(node);
  if (!call || !ts.isCallExpression(call) || !ts.isIdentifier(call.expression) ||
      call.expression.text !== 'defineStore') return;
  const idArg = call.arguments[0];
  const storeId = idArg && (ts.isStringLiteral(idArg) || ts.isNoSubstitutionTemplateLiteral(idArg))
    ? idArg.text : 'unknown';
  const definition = call.arguments[1];
  add('store:' + storeId, 'Pinia store ' + storeId, call);
  if (definition && (ts.isArrowFunction(definition) || ts.isFunctionExpression(definition))) {
    collectSetupStore(definition, storeId);
    return;
  }
  const properties = objectProperties(definition);
  for (const property of properties) {
    const key = nameOf(property.name);
    if (!key) continue;
    if (key === 'state') {
      const initializer = ts.isPropertyAssignment(property) ? unwrap(property.initializer) : null;
      const stateObject = initializer && ts.isCallExpression(initializer)
        ? initializer.arguments[0] : initializer;
      for (const state of objectProperties(stateObject)) {
        const stateName = nameOf(state.name);
        if (stateName) add('store-state:' + stateName, 'state ' + stateName + ': ' + textOf(state), state);
      }
    }
    if (key === 'getters' || key === 'actions') {
      for (const item of objectProperties(ts.isPropertyAssignment(property) ? property.initializer : null)) {
        const itemName = nameOf(item.name);
        if (itemName) add('store-' + (key === 'getters' ? 'getter:' : 'action:') + itemName,
          (key === 'getters' ? 'getter ' : 'action ') + itemName + ': ' + textOf(item), item);
      }
    }
  }
};
const visit = node => {
  if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) imports.push(node.moduleSpecifier.text);
  if (ts.isCallExpression(node)) {
    if (ts.isIdentifier(node.expression) && node.expression.text === 'defineStore') inspectStore(node);
    if (ts.isIdentifier(node.expression)) {
      const call = node.expression.text;
      if (call === 'inject' || call === 'fetch' || call === 'axios' || call === 'useFetch' ||
          call === 'useRouter' || call === 'useRoute' || call === 'useI18n' ||
          (call.startsWith('use') && call.endsWith('Store'))) dependencies.add(call);
    }
  }
  ts.forEachChild(node, visit);
};
for (const statement of ast.statements) {
  if (!isExported(statement)) continue;
  if (ts.isFunctionDeclaration(statement) && statement.name) {
    const name = statement.name.text;
    add('export:' + name, 'export function ' + name + '(' +
      (statement.parameters ?? []).map(p => textOf(p)).join(', ') + '): ' +
      (statement.type ? textOf(statement.type) : 'unknown'), statement);
    if (name.startsWith('use')) {
      const visitComposable = node => {
        if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)) {
          const call = node.expression.text;
          if (call === 'inject' || call === 'fetch' || call === 'axios' || call === 'useFetch' ||
              call === 'useRouter' || call === 'useRoute' || call === 'useI18n' ||
              (call.startsWith('use') && call.endsWith('Store'))) dependencies.add(call);
        }
        ts.forEachChild(node, visitComposable);
      };
      visitComposable(statement);
    }
  } else if (ts.isVariableStatement(statement)) {
    for (const declaration of statement.declarationList.declarations) {
      const name = nameOf(declaration);
      if (!name) continue;
      const init = unwrap(declaration.initializer);
      if (init && ts.isCallExpression(init) && ts.isIdentifier(init.expression) && init.expression.text === 'defineStore') {
        add('export:' + name, 'export Pinia store ' + name, declaration);
      } else {
        add('export:' + name, 'export const ' + name + (declaration.type ? ': ' + textOf(declaration.type) : '') +
          (init ? ' = ' + textOf(init) : ''), declaration);
      }
    }
  } else if ((ts.isClassDeclaration(statement) || ts.isInterfaceDeclaration(statement) ||
      ts.isTypeAliasDeclaration(statement)) && statement.name) {
    add('export:' + statement.name.text, 'export ' + textOf(statement), statement);
  }
}
visit(ast);
const targetName = path.basename(filename, path.extname(filename));
const storeMember = members.find(m => m.id.startsWith('store:'));
const storeSeen = !!storeMember;
const displayName = storeMember ? storeMember.id.slice('store:'.length) : targetName;
if (!storeSeen && !targetName.startsWith('use') && !/(store|pinia)/i.test(targetName)) {
  console.error('TypeScript target is not recognized as a composable or Pinia store.');
  process.exit(2);
}
const ignored = new Set(['node_modules', 'dist', 'build', '.git', 'coverage']);
const testFiles = [];
const walk = directory => {
  let entries;
  try { entries = fs.readdirSync(directory, { withFileTypes: true }); } catch { return; }
  for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
    if (entry.isDirectory()) { if (!ignored.has(entry.name)) walk(path.join(directory, entry.name)); }
    else if (entry.isFile() && /\.(?:spec|test)\.(?:ts|tsx|js|jsx|mjs|cjs)$/.test(entry.name))
      testFiles.push(path.join(directory, entry.name));
  }
};
walk(root);
const relatedTests = [];
for (const testFile of testFiles) {
  const base = path.basename(testFile);
  let matchedOn = null;
  let line = 1;
  if (base.startsWith(targetName + '.')) matchedOn = 'basename';
  else {
    let testSource = '';
    try { testSource = fs.readFileSync(testFile, 'utf8'); } catch { continue; }
    const testAst = ts.createSourceFile(testFile, testSource, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    const visitImports = node => {
      if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
        const specifier = node.moduleSpecifier.text;
        const importedBase = path.basename(specifier.replace(/\.(?:tsx?|jsx?)$/, ''));
        if (specifier === './' + targetName || specifier.endsWith('/' + targetName) ||
            importedBase === targetName || specifier.endsWith('/' + targetName + '.ts')) {
          matchedOn = 'import'; line = testAst.getLineAndCharacterOfPosition(node.getStart(testAst)).line + 1;
        }
      }
      ts.forEachChild(node, visitImports);
    };
    visitImports(testAst);
  }
  if (matchedOn) relatedTests.push({ name: base, file: path.relative(root, testFile).split(path.sep).join('/'), line, matchedOn });
}
relatedTests.sort((a, b) => a.file.localeCompare(b.file));
console.log(JSON.stringify({
  name: displayName,
  kind: storeSeen ? 'pinia-store' : 'composable',
  imports: [...new Set(imports)].sort(),
  dependencies: [...dependencies].sort(),
  members: members.sort((a, b) => a.id.localeCompare(b.id)),
  relatedTests,
}));
