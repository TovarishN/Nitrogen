// Compares the scope a TextMate grammar paints with the scope the server's semantic tokens give each range
// (`nitrogen tokens`), so the switch from the first frame to semantic tokens shows no colour change.
// usage: node compare-scopes.mjs --grammar <tmLanguage.json> --scope-name <source.x> --source <file> --tokens <tokens.json> [--allow <file>] [--exact]
// A server token mismatches when the innermost non-meta TextMate scope at its start is neither a prefix of the
// server's scope nor extended by it (language suffixes stripped). Ranges TextMate leaves unpainted are the
// server's to fill in, and embedded C# (source.cs) is not compared. --exact requires the very same scope (the
// hand-written .ngr grammar, which the server matches scope for scope). --allow lists accepted `line:character`
// entries, one per line, `#` comments allowed.
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as path from 'node:path';

const require = createRequire(import.meta.url);
const oniguruma = require('vscode-oniguruma');
const textmate = require('vscode-textmate');
const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, arg, i, all) => (arg.startsWith('--') ? [...pairs, [arg.slice(2), all[i + 1]?.startsWith('--') ? true : all[i + 1] ?? true]] : pairs), []));
for (const required of ['grammar', 'scope-name', 'source', 'tokens'])
  if (!args[required]) { console.error(`missing --${required}`); process.exit(2); }

const wasm = fs.readFileSync(path.join(path.dirname(require.resolve('vscode-oniguruma')), 'onig.wasm'));
await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
const registry = new textmate.Registry({
  onigLib: Promise.resolve({ createOnigScanner: s => new oniguruma.OnigScanner(s), createOnigString: s => new oniguruma.OnigString(s) }),
  loadGrammar: async scope => scope === args['scope-name'] ? textmate.parseRawGrammar(fs.readFileSync(args.grammar, 'utf8'), args.grammar) : null,
});
const grammar = await registry.loadGrammar(args['scope-name']);
const suffix = '.' + args['scope-name'].replace(/^source\./, '');
const strip = scope => scope.endsWith(suffix) ? scope.slice(0, -suffix.length) : scope;
const allowed = new Set(args.allow ? fs.readFileSync(args.allow, 'utf8').split('\n').map(l => l.replace(/#.*/, '').trim()).filter(Boolean) : []);

const lines = fs.readFileSync(args.source, 'utf8').split(/\r?\n/);
const painted = [];
let state = textmate.INITIAL;
for (const line of lines) {
  const result = grammar.tokenizeLine(line, state);
  painted.push(result.tokens);
  state = result.ruleStack;
}

/** The innermost non-meta scope TextMate gives `character` of `line`; null when unpainted or embedded C#. */
function scopeAt(line, character) {
  const token = painted[line]?.find(t => t.startIndex <= character && character < t.endIndex);
  if (!token || token.scopes.some(s => s.endsWith('.cs') || s === 'source.cs')) return null;
  const scope = [...token.scopes].reverse().find(s => !s.startsWith('meta.'));
  return !scope || scope === args['scope-name'] ? null : strip(scope);
}

let mismatches = 0;
for (const token of JSON.parse(fs.readFileSync(args.tokens, 'utf8'))) {
  const textmateScope = scopeAt(token.line, token.character);
  if (textmateScope === null || allowed.has(`${token.line + 1}:${token.character + 1}`)) continue;
  const server = strip(token.scope);
  // A string's opening or closing punctuation takes the string's colour in themes; the server paints the whole string.
  if (textmateScope.startsWith('punctuation.definition.string.') && server.startsWith('string.')) continue;
  if (server === textmateScope || !args.exact && (server.startsWith(textmateScope + '.') || textmateScope.startsWith(server + '.'))) continue;
  const text = lines[token.line].slice(token.character, token.character + token.length);
  console.log(`${args.source}:${token.line + 1}:${token.character + 1} ${JSON.stringify(text)}  textmate=${textmateScope} server=${server}`);
  mismatches++;
}
if (mismatches > 0) { console.log(`${mismatches} mismatch(es)`); process.exit(1); }
