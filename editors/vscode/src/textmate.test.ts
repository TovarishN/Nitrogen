import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as oniguruma from 'vscode-oniguruma';
import * as textmate from 'vscode-textmate';

// A grammar `nitrogen generate textmate` wrote (examples/DateCalc): every regex must compile in Oniguruma, VS Code's engine.
const fixtures = path.join(__dirname, '..', '..', 'test-fixtures');

async function datecalc(): Promise<textmate.IGrammar> {
  const wasm = fs.readFileSync(path.join(path.dirname(require.resolve('vscode-oniguruma')), 'onig.wasm'));
  await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
  const registry = new textmate.Registry({
    onigLib: Promise.resolve({ createOnigScanner: sources => new oniguruma.OnigScanner(sources), createOnigString: text => new oniguruma.OnigString(text) }),
    loadGrammar: async () => textmate.parseRawGrammar(fs.readFileSync(path.join(fixtures, 'datecalc.tmLanguage.json'), 'utf8'), 'datecalc.tmLanguage.json'),
  });
  const grammar = await registry.loadGrammar('source.datecalc');
  assert.ok(grammar);
  return grammar;
}

/** The innermost scope of the token starting at `text`'s first occurrence in `line`. */
function scopeAt(grammar: textmate.IGrammar, line: string, text: string): string {
  const start = line.indexOf(text);
  const token = grammar.tokenizeLine(line, textmate.INITIAL).tokens.find(t => t.startIndex === start);
  assert.ok(token, `no token starts at '${text}'`);
  return token.scopes[token.scopes.length - 1];
}

test('a generated grammar paints declarations, keywords and numbers', async () => {
  const grammar = await datecalc();
  const line = 'let start = 2 weeks; // note';
  assert.equal(scopeAt(grammar, line, 'let'), 'storage.type.datecalc');
  assert.equal(scopeAt(grammar, line, 'start'), 'variable.other.readwrite.datecalc');
  assert.equal(scopeAt(grammar, line, ';'), 'punctuation.terminator.datecalc');
  assert.equal(scopeAt(grammar, line, '// note'), 'comment.line.double-slash.datecalc');
});
