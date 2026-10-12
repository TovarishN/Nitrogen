import * as fs from 'fs';
import * as path from 'path';
import { ExtensionContext, extensions, window, workspace } from 'vscode';
import { DocumentSelector, LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { CSharpStrings, csharpSelector } from './csharpStrings';
import { languagesOfOtherExtensions, workspaceFileExtensions } from './otherExtensions';
import { serverCommand } from './server';

let client: LanguageClient | undefined;
let strings: CSharpStrings | undefined;

/** The file extensions nitrogen.json declares for workspace grammar languages. */
function workspaceExtensions(claimedLanguages: readonly string[]): string[] {
  const root = workspace.workspaceFolders?.[0]?.uri.fsPath;
  if (!root) return [];
  try {
    return workspaceFileExtensions(fs.readFileSync(path.join(root, 'nitrogen.json'), 'utf8'), claimedLanguages);
  } catch {
    return [];
  }
}

export async function activate(context: ExtensionContext): Promise<void> {
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
    ...workspaceExtensions(claimedLanguages).map(extension => ({ scheme: 'file', pattern: `**/*${extension}` })),
    ...csharpSelector,
  ];
  strings = new CSharpStrings(() => client);
  const clientOptions: LanguageClientOptions = {
    documentSelector: selector,
    middleware: strings.middleware,
    // Generated extensions own their language files and tagged C# strings; this client still serves .ngr.
    initializationOptions: { skipLanguages: claimedLanguages },
    synchronize: { fileEvents: workspace.createFileSystemWatcher('**/{*.ngr,nitrogen.json}') },
  };
  client = new LanguageClient('nitrogen', 'Nitrogen', serverOptions, clientOptions);
  await client.start();
}

export function deactivate(): Thenable<void> | undefined {
  strings?.dispose();
  return client?.stop();
}
