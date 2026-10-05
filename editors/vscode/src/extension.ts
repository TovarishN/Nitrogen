import * as fs from 'fs';
import * as path from 'path';
import { ExtensionContext, extensions, workspace } from 'vscode';
import { DocumentSelector, LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { CSharpStrings, csharpSelector } from './csharpStrings';
import { languagesOfOtherExtensions } from './otherExtensions';

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
