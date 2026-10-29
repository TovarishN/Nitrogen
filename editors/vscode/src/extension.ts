import * as fs from 'fs';
import * as path from 'path';
import { ExtensionContext, extensions, window, workspace } from 'vscode';
import { DocumentSelector, LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { CSharpStrings, csharpSelector } from './csharpStrings';
import { languagesOfOtherExtensions, workspaceFileExtensions } from './otherExtensions';
import { SignatureRouting } from './signatureRouting';
import { serverCommand } from './server';
import { registerGrammarExtraction } from './extractGrammar';
import { nitrideInstalled } from './nitride';

let client: LanguageClient | undefined;
let strings: CSharpStrings | undefined;

/** The file extensions nitrogen.json declares for workspace grammar languages. */
function workspaceExtensions(claimedLanguages: readonly string[]): string[] {
  return [...new Set((workspace.workspaceFolders ?? []).flatMap(folder => {
    try {
      return workspaceFileExtensions(fs.readFileSync(path.join(folder.uri.fsPath, 'nitrogen.json'), 'utf8'), claimedLanguages);
    } catch { return []; }
  }))];
}

export async function activate(context: ExtensionContext): Promise<void> {
  if (nitrideInstalled(id => extensions.getExtension(id))) return;
  const settings = workspace.getConfiguration('nitrogen');
  const server = serverCommand({
    serverPath: settings.get<string>('server.path') ?? '',
    dotnetPath: settings.get<string>('dotnetPath') ?? '',
    extensionPath: context.extensionPath,
    env: process.env,
    platform: process.platform,
    exists: file => fs.existsSync(file),
  });
  if ('error' in server) {
    void window.showErrorMessage(server.error);
    return;
  }
  const serverOptions: ServerOptions = { command: server.command, args: server.args };
  const claimedLanguages = languagesOfOtherExtensions(extensions.all, context.extension.id);
  const selector: DocumentSelector = [
    { scheme: 'file', language: 'ngr' },
    { scheme: 'file', language: 'nitrogen-signature' },
    { scheme: 'file', pattern: '**/*' },
    { scheme: 'untitled' },
    ...workspaceExtensions(claimedLanguages).map(extension => ({ scheme: 'file', pattern: `**/*${extension}` })),
    ...csharpSelector,
  ];
  strings = new CSharpStrings(() => client);
  const routing = new SignatureRouting(true);
  const clientOptions: LanguageClientOptions = {
    documentSelector: selector,
    middleware: routing.middleware(strings.middleware, document => (document.uri.scheme === 'file' || document.uri.scheme === 'untitled') && !claimedLanguages.some(name => document.languageId.toLowerCase() === name.toLowerCase() || document.uri.path.toLowerCase().endsWith('.' + name.toLowerCase()))),
    // Generated extensions own their language files and tagged C# strings; this client still serves .ngr.
    initializationOptions: { skipLanguages: claimedLanguages, grammarExtraction: true, paletteTokens: true },
    synchronize: { fileEvents: workspace.createFileSystemWatcher('**/*') },
  };
  if (clientOptions.synchronize?.fileEvents && !Array.isArray(clientOptions.synchronize.fileEvents)) context.subscriptions.push(clientOptions.synchronize.fileEvents);
  context.subscriptions.push(strings);
  client = new LanguageClient('nitrogen', 'Nitrogen', serverOptions, clientOptions);
  await client.start();
  strings.refreshVisible();
  registerGrammarExtraction(context, client);
  routing.start(context);
}

export function deactivate(): Thenable<void> | undefined {
  strings?.dispose();
  return client?.stop();
}
