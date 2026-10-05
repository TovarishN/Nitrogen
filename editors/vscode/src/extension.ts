import * as fs from 'fs';
import * as path from 'path';
import { ExtensionContext, workspace } from 'vscode';
import { DocumentSelector, LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { CSharpStrings, csharpSelector } from './csharpStrings';

let client: LanguageClient | undefined;
let strings: CSharpStrings | undefined;

/** The file extensions nitrogen.json declares for workspace grammar languages. */
function workspaceExtensions(): string[] {
  const root = workspace.workspaceFolders?.[0]?.uri.fsPath;
  if (!root) return [];
  try {
    const config = JSON.parse(fs.readFileSync(path.join(root, 'nitrogen.json'), 'utf8'));
    return (config.languages ?? []).flatMap((language: { extensions?: string[] }) => language.extensions ?? []);
  } catch {
    return [];
  }
}

export async function activate(_context: ExtensionContext): Promise<void> {
  const command = workspace.getConfiguration('nitrogen').get<string>('server.path') || 'nitrogen';
  const serverOptions: ServerOptions = { command, args: ['lsp'] };
  const selector: DocumentSelector = [
    { scheme: 'file', language: 'ngr' },
    ...workspaceExtensions().map(extension => ({ scheme: 'file', pattern: `**/*${extension}` })),
    ...csharpSelector,
  ];
  strings = new CSharpStrings(() => client);
  const clientOptions: LanguageClientOptions = {
    documentSelector: selector,
    middleware: strings.middleware,
    synchronize: { fileEvents: workspace.createFileSystemWatcher('**/{*.ngr,nitrogen.json}') },
  };
  client = new LanguageClient('nitrogen', 'Nitrogen', serverOptions, clientOptions);
  await client.start();
}

export function deactivate(): Thenable<void> | undefined {
  strings?.dispose();
  return client?.stop();
}
