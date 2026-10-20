import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import Module = require('node:module');
const edits: ((event: any) => void)[] = [];
const visible: (() => void)[] = [];
const painted: any[] = [];
const document = { languageId: 'csharp', version: 1, uri: { toString: () => 'file:///Snippets.cs' } };
const editor = { document, setDecorations: (_: any, ranges: any[]) => painted.push(ranges) };
const listen = (callbacks: any[]) => (callback: any) => { callbacks.push(callback); return { dispose() {} }; };
const vscode = {
  window: { visibleTextEditors: [editor], onDidChangeVisibleTextEditors: listen(visible), createTextEditorDecorationType: () => ({ dispose() {} }) },
  workspace: { onDidChangeTextDocument: listen(edits), onDidCloseTextDocument: () => ({ dispose() {} }) },
  Range: class { constructor(public line: number, public start: number, public endLine: number, public end: number) {} },
  SemanticTokens: class { constructor(public data: Uint32Array) {} },
  ThemeColor: class { constructor(public id: string) {} },
};
const loader = (Module as any)._load;
(Module as any)._load = function (id: string, ...args: any[]) { return id === 'vscode' ? vscode : loader.call(this, id, ...args); };
const settle = () => new Promise(resolve => setImmediate(resolve));
test('embedded colours refresh without selecting the Nitrogen token provider', async () => {
  const { CSharpStrings } = await import('./csharpStrings');
  let requests = 0;
  const client = { initializeResult: { capabilities: { semanticTokensProvider: { legend: { tokenTypes: ['keyword'] } } } },
    async sendRequest(method: string, params: any) {
      assert.equal(method, 'textDocument/semanticTokens/full');
      assert.equal(params.textDocument.uri, document.uri.toString());
      requests++;
      return { data: [9, 8, 3, 0, 0] };
    } };
  const strings = new CSharpStrings(() => client as any);
  for (const callback of visible) callback();
  await settle();
  assert.equal(requests, 1);
  assert.ok(painted.some(ranges => ranges.length === 1));
  document.version++;
  for (const callback of edits) callback({ document });
  await settle();
  assert.equal(requests, 2);
  strings.dispose();
});
