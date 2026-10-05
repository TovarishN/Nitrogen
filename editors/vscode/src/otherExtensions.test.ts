import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';
import { languagesIn, languagesOfOtherExtensions } from './otherExtensions';

const dateCalc = JSON.stringify({
  languages: [{ name: 'DateCalc', extensions: ['.datecalc', '.dc'], grammars: ['DateCalc.ngr'], namespace: 'DateCalc.Syntax' }],
});

test('a nitrogen.json names its languages and their extensions', () => {
  assert.deepEqual(languagesIn(dateCalc), ['DateCalc', 'datecalc', 'dc']);
  assert.deepEqual(languagesIn('not json'), []);
});

test('only other Nitrogen extensions with a bundled language count', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nitrogen-extensions-'));
  const extension = (name: string, config?: string) => {
    const dir = path.join(root, name);
    fs.mkdirSync(path.join(dir, 'bundle', 'language'), { recursive: true });
    if (config) fs.writeFileSync(path.join(dir, 'bundle', 'language', 'nitrogen.json'), config);
    return { id: name, extensionPath: dir };
  };
  const all = [
    extension('nitrogen.nitrogen', JSON.stringify({ languages: [{ name: 'Own', extensions: ['.own'] }] })),
    extension('nitrogen.nitrogen-datecalc', dateCalc),
    extension('nitrogen.nitrogen-empty'),
    extension('someone.nitrogen-lookalike', JSON.stringify({ languages: [{ name: 'Other' }] })),
  ];
  assert.deepEqual(languagesOfOtherExtensions(all, 'nitrogen.nitrogen'), ['DateCalc', 'datecalc', 'dc']);
  fs.rmSync(root, { recursive: true });
});
