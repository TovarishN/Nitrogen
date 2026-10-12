import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { signatureDocumentMode, signatureLanguageId, ownsSignatureDocuments } from './signatureDocuments';

test('leading signatures accept comments and blank lines but not source strings', () => {
  assert.equal(signatureDocumentMode('// #!nitrogen augment "./Rules.syntax"\nclass C {}'), 'augment');
  assert.equal(signatureDocumentMode('#!nitrogen language "./Calc.syntax"\n1 + 2'), 'language');
  assert.equal(signatureDocumentMode('const s = "#!nitrogen language Calc";'), undefined);
  assert.equal(signatureDocumentMode('\ufeff\n/* comment */\n<!-- #!nitrogen augment "./Rules.syntax" regions -->'), 'augment');
  assert.equal(signatureDocumentMode('/*\n#!nitrogen grammar\n*/\nlanguage L {}'), 'grammar');
  assert.equal(signatureDocumentMode('// documentation\n\n#!nitrogen language L'), 'language');
});
test('primary IDs change, native augmentation IDs stay native', () => {
  assert.equal(signatureLanguageId('augment', 'csharp'), 'csharp');
  assert.equal(signatureLanguageId('augment', 'razor'), 'razor');
  assert.equal(signatureLanguageId('language', 'plaintext'), 'nitrogen-signature');
  assert.equal(signatureLanguageId('grammar', 'plaintext'), 'ngr');
});
test('the main extension owns signatures with mixed installations', () => {
  assert.equal(ownsSignatureDocuments('nitrogen.nitrogen', ['nitrogen.nitrogen', 'nitrogen.nitrogen-datecalc']), true);
  assert.equal(ownsSignatureDocuments('nitrogen.nitrogen-datecalc', ['nitrogen.nitrogen', 'nitrogen.nitrogen-datecalc']), false);
  assert.equal(ownsSignatureDocuments('nitrogen.nitrogen-datecalc', ['nitrogen.nitrogen-datecalc']), true);
  assert.equal(ownsSignatureDocuments('nitrogen.nitrogen-z', ['nitrogen.nitrogen-z', 'nitrogen.nitrogen-a']), false);
});

for (const newline of ['\n', '\r\n', '\r']) {
  test(`comment prose before a signature uses server header semantics (${JSON.stringify(newline)})`, () => {
    assert.equal(signatureDocumentMode(['/*', 'Documentation', '#!nitrogen language "./Calc.syntax"', '*/'].join(newline)), 'language');
  });
}
