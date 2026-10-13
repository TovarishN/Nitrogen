import * as assert from 'node:assert/strict';
import { test } from 'node:test';
import { extractGrammar, ExtractionHost, ExtractionSelection, extractionPreview, confirmedExtractionEdit } from './extractGrammarFlow';

const selection: ExtractionSelection = { textDocument: { uri: 'file:///a.ngr' }, range: { start: { line: 0, character: 18 }, end: { line: 0, character: 36 } } };
function fixture() {
  const calls: string[] = [];
  const host: ExtractionHost = {
    prepare: async () => ({ id: 'proposal', suggestedModule: 'A.Extracted', suggestedFileName: 'Extracted.ngr' }),
    destination: async () => 'file:///new.ngr',
    moduleName: async () => 'Shared',
    resolve: async request => { calls.push('resolve:' + request.module); return { action: { edit: {} }, expectedDocuments: [], notices: [] }; },
    review: async () => { calls.push('review'); return true; },
    apply: async () => { calls.push('apply'); return true; },
    open: async uri => { calls.push('open:' + uri); },
    warn: message => { calls.push('warn:' + message); },
  };
  return { host, calls };
}
test('collects inputs, applies a reviewed proposal, and opens the destination', async () => {
  const { host, calls } = fixture();
  await extractGrammar(host, selection);
  assert.deepEqual(calls, ['resolve:Shared', 'review', 'resolve:Shared', 'apply', 'open:file:///new.ngr']);
});
test('cancelling the destination does not resolve or apply', async () => {
  const { host, calls } = fixture(); host.destination = async () => undefined;
  await extractGrammar(host, selection); assert.deepEqual(calls, []);
});
test('cancelling the module prompt does not resolve or apply', async () => {
  const { host, calls } = fixture(); host.moduleName = async () => undefined;
  await extractGrammar(host, selection); assert.deepEqual(calls, []);
});
test('preparation refusal explains why and never opens prompts', async () => {
  const { host, calls } = fixture(); host.prepare = async () => ({ unavailableReason: 'Select complete rules.' });
  host.destination = async () => { throw Error('unexpected prompt'); };
  await extractGrammar(host, selection); assert.deepEqual(calls, ['warn:Select complete rules.']);
});
test('changed documents while prompts are open are refused by resolution', async () => {
  const { host, calls } = fixture(); host.resolve = async () => ({ unavailableReason: 'Workspace changed.', expectedDocuments: [], notices: [] });
  await extractGrammar(host, selection); assert.deepEqual(calls, ['warn:Workspace changed.']);
});
test('a cancelled or failed preview does not open the destination', async () => {
  const { host, calls } = fixture(); host.apply = async () => false;
  await extractGrammar(host, selection); assert.deepEqual(calls, ['resolve:Shared', 'review', 'resolve:Shared']);
});
test('transport errors explain failure without applying', async () => {
  const { host, calls } = fixture(); host.resolve = async () => { throw Error('server unavailable'); };
  await extractGrammar(host, selection); assert.deepEqual(calls, ['warn:Extraction failed: server unavailable']);
});

test('cancelling whole-proposal review never applies a subset', async () => {
  const { host, calls } = fixture(); host.review = async () => { calls.push('review'); return false; };
  await extractGrammar(host, selection);
  assert.deepEqual(calls, ['resolve:Shared', 'review']);
});

test('review snapshots include creation, removal, references and config, without per-edit confirmation', () => {
  const result = {
    notices: [], expectedDocuments: [{ uri: 'file:///a.ngr', text: 'rule\r\nuse', version: 2 }, { uri: 'file:///nitrogen.json', text: '[]' }],
    action: { edit: { changeAnnotations: { extract: { label: 'Extract', needsConfirmation: true } }, documentChanges: [
      { kind: 'create' as const, uri: 'file:///new.ngr', annotationId: 'extract' },
      { textDocument: { uri: 'file:///new.ngr', version: null }, edits: [{ range: { start: { line: 0, character: 0 }, end: { line: 0, character: 0 } }, newText: 'rule', annotationId: 'extract' }] },
      { textDocument: { uri: 'file:///a.ngr', version: 2 }, edits: [
        { range: { start: { line: 0, character: 0 }, end: { line: 1, character: 0 } }, newText: '', annotationId: 'extract' },
        { range: { start: { line: 1, character: 0 }, end: { line: 1, character: 3 } }, newText: 'Shared.rule', annotationId: 'extract' }] },
      { textDocument: { uri: 'file:///nitrogen.json', version: null }, edits: [{ range: { start: { line: 0, character: 1 }, end: { line: 0, character: 1 } }, newText: '"new.ngr"', annotationId: 'extract' }] },
    ] } },
  };
  assert.deepEqual(extractionPreview(result), [
    { uri: 'file:///new.ngr', before: '', after: 'rule' },
    { uri: 'file:///a.ngr', before: 'rule\r\nuse', after: 'Shared.rule' },
    { uri: 'file:///nitrogen.json', before: '[]', after: '["new.ngr"]' },
  ]);
  const edit = confirmedExtractionEdit(result);
  assert.equal(edit.changeAnnotations, undefined);
  assert.equal(JSON.stringify(edit).includes('annotationId'), false);
});

test('a dependency changed during review refuses application', async () => {
  const { host, calls } = fixture();
  const resolve = host.resolve;
  let count = 0;
  host.resolve = async request => ++count === 1 ? resolve(request)
    : { unavailableReason: 'Workspace changed during review.', notices: [], expectedDocuments: [] };
  await extractGrammar(host, selection);
  assert.deepEqual(calls, ['resolve:Shared', 'review', 'warn:Workspace changed during review.']);
});
