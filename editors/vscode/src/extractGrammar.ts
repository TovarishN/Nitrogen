import * as path from 'path';
import { commands, ExtensionContext, Uri, window, workspace } from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { extractGrammar, ExtractionHost, ExtractionPreparation, ExtractionResolution, ExtractionSelection, extractionPreview, confirmedExtractionEdit } from './extractGrammarFlow';

export function registerGrammarExtraction(context: ExtensionContext, client: LanguageClient): void {
  const previews = new Map<string, string>();
  let previewId = 0;
  context.subscriptions.push(workspace.registerTextDocumentContentProvider('nitrogen-extraction', {
    provideTextDocumentContent: uri => previews.get(uri.toString()) ?? '',
  }));
  context.subscriptions.push(commands.registerCommand('nitrogen.extractGrammar', async (argument?: ExtractionSelection) => {
    const editor = window.activeTextEditor;
    const selection = argument ?? (editor && {
      textDocument: { uri: editor.document.uri.toString() },
      range: { start: editor.selection.start, end: editor.selection.end },
    });
    if (!selection) { void window.showWarningMessage('Select complete grammar definitions first.'); return; }
    const host: ExtractionHost = {
      prepare: value => client.sendRequest<ExtractionPreparation>('nitrogen/prepareGrammarExtraction', value),
      destination: async fileName => {
        const source = Uri.parse(selection.textDocument.uri);
        const selected = await window.showSaveDialog({
          title: 'Extract Block — destination grammar', saveLabel: 'Choose destination',
          defaultUri: source.with({ path: path.posix.join(path.posix.dirname(source.path), fileName) }),
          filters: { 'Nitrogen grammar': ['ngr'] },
        });
        return selected?.toString();
      },
      moduleName: suggested => Promise.resolve(window.showInputBox({
        title: 'Extract Block — module name', value: suggested, prompt: 'Name of the reusable syntax module',
        validateInput: value => /^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$/.test(value)
          ? undefined : 'Use a module name such as Shared.Expressions.',
      })),
      resolve: request => client.sendRequest<ExtractionResolution>('nitrogen/resolveGrammarExtraction', request),
      review: async result => {
        previews.clear();
        const changes = extractionPreview(result);
        const group = ++previewId;
        const diffs = changes.map((change, index) => {
          const name = path.posix.basename(Uri.parse(change.uri).path);
          const before = Uri.from({ scheme: 'nitrogen-extraction', path: `/${group}/${index}/before/${name}` });
          const after = Uri.from({ scheme: 'nitrogen-extraction', path: `/${group}/${index}/after/${name}` });
          previews.set(before.toString(), change.before);
          previews.set(after.toString(), change.after);
          return [Uri.parse(change.uri), before, after];
        });
        await commands.executeCommand('vscode.changes', 'Extract Block — review all changes', diffs);
        const choice = await window.showInformationMessage(
          `Review all ${changes.length} files, then apply the complete extraction. ${result.notices.join(' ')}`,
          'Apply all changes', 'Cancel');
        return choice === 'Apply all changes';
      },
      apply: async result => {
        const converted = await client.protocol2CodeConverter.asWorkspaceEdit(confirmedExtractionEdit(result));
        // VS Code's public WorkspaceEdit does not carry LSP version numbers. Check the server's
        // snapshots after review, then apply the complete edit in this same event-loop turn.
        const documents = await Promise.all(result.expectedDocuments.map(expected => workspace.openTextDocument(Uri.parse(expected.uri))));
        for (let i = 0; i < documents.length; i++) {
          const expected = result.expectedDocuments[i], actual = documents[i];
          if (actual.getText() !== expected.text || (expected.version != null && actual.version !== expected.version)) {
            void window.showWarningMessage('A file changed before extraction could be reviewed. Select the definitions again.');
            return false;
          }
        }
        const applied = await workspace.applyEdit(converted);
        if (!applied) void window.showInformationMessage('Extraction was cancelled or could not be applied. Review the files before retrying.');
        return applied;
      },
      open: async uri => { await window.showTextDocument(Uri.parse(uri), { preview: false }); },
      warn: message => { void window.showWarningMessage(message); },
    };
    await extractGrammar(host, selection);
  }));
}
