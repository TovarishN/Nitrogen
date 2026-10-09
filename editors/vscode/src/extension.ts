import * as fs from 'fs';
import * as path from 'path';
import { ExtensionContext, extensions, window, workspace } from 'vscode';
import { DocumentSelector, LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { CSharpStrings, csharpSelector } from './csharpStrings';
import { languagesOfOtherExtensions } from './otherExtensions';
import { serverCommand } from './server';

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
  const selector: DocumentSelector = [
    { scheme: 'file', language: 'ngr' },
    ...workspaceExtensions().map(extension => ({ scheme: 'file', pattern: `**/*${extension}` })),
    ...csharpSelector,
  ];
  strings = new CSharpStrings(() => client);
  const clientOptions: LanguageClientOptions = {
    documentSelector: selector,
    middleware: strings.middleware,
    // This server reads the workspace's nitrogen.json: the C# strings of languages other extensions carry are theirs.
    initializationOptions: { skipLanguages: languagesOfOtherExtensions(extensions.all, context.extension.id) },
    synchronize: { fileEvents: workspace.createFileSystemWatcher('**/{*.ngr,nitrogen.json}') },
  };
  client = new LanguageClient('nitrogen', 'Nitrogen', serverOptions, clientOptions);
  await client.start();
}

export function deactivate(): Thenable<void> | undefined {
  strings?.dispose();
  return client?.stop();
}
