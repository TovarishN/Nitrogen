import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import Module = require('node:module');

// Exercise the adapter against VS Code's event contract, including close/open on an ID change.
const opened: ((document: any) => void)[] = [];
const changed: ((event: any) => void)[] = [];
const closed: ((document: any) => void)[] = [];
const documents: any[] = [];
const listen = (callbacks: any[]) => (callback: any) => { callbacks.push(callback); return { dispose() {} }; };
const vscode = {
  workspace: { textDocuments: documents, onDidOpenTextDocument: listen(opened), onDidChangeTextDocument: listen(changed), onDidCloseTextDocument: listen(closed) },
  languages: { async setTextDocumentLanguage(document: any, id: string) {
    for (const callback of closed) callback(document);
    document.languageId = id;
    for (const callback of opened) callback(document);
    return document;
  } },
};
const loader = (Module as any)._load;
(Module as any)._load = function (id: string, ...args: any[]) { return id === 'vscode' ? vscode : loader.call(this, id, ...args); };
const settle = () => new Promise(resolve => setImmediate(resolve));

test('startup, insertion, and removal route documents in both roots and preserve native IDs', async () => {
  const { SignatureRouting } = await import('./signatureRouting');
  const doc = (path: string, languageId: string, text: string) => ({ uri: { scheme: 'file', toString: () => path }, languageId, text, getText() { return this.text; } });
  const a = doc('file:///one/a.txt', 'plaintext', '#!nitrogen language L\nsource');
  const b = doc('file:///two/Host.cs', 'csharp', '// #!nitrogen augment Rules\nclass C {}');
  documents.push(a, b);
  const routing = new SignatureRouting(true);
  routing.start({ subscriptions: [] } as any);
  await settle();
  assert.equal(a.languageId, 'nitrogen-signature');
  assert.equal(b.languageId, 'csharp');
  a.text = 'plain text';
  for (const callback of changed) callback({ document: a });
  await settle();
  assert.equal(a.languageId, 'plaintext');
  a.text = '#!nitrogen grammar\nlanguage L {}';
  for (const callback of changed) callback({ document: a });
  await settle();
  assert.equal(a.languageId, 'ngr');
  const middleware = routing.middleware({}, () => true) as any;
  let calls = 0;
  assert.equal(middleware.provideDefinition(b, null, null, () => ++calls), null);
  assert.equal(calls, 0);
  assert.equal(middleware.provideDefinition(a, null, null, () => ++calls), 1);
  assert.equal(new SignatureRouting(false).middleware({}, () => true).provideDefinition!(a as any, null as any, null as any, (() => ++calls) as any), null);
  routing.dispose();
});

test('signed untitled documents route and restore their original mode', async () => {
  const { SignatureRouting } = await import('./signatureRouting');
  const document = {uri:{scheme:'untitled',toString:()=> 'untitled:Untitled-1'},languageId:'plaintext',text:'#!nitrogen language L\ntext',getText(){return this.text;}};
  documents.push(document);
  const routing = new SignatureRouting(true);routing.start({subscriptions:[]} as any);await settle();
  assert.equal(document.languageId,'nitrogen-signature');
  document.text='plain';for(const callback of changed)callback({document});await settle();
  assert.equal(document.languageId,'plaintext');routing.dispose();
});
