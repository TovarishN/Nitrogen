import * as path from 'path';
import { commands, ExtensionContext, StatusBarAlignment, Uri, window, workspace } from 'vscode';
import type { LanguageClient } from 'vscode-languageclient/node';
import { extractQuoteHelper, QuoteHelperPreparation, extractGrammar, ExtractionHost, ExtractionPreparation, ExtractionResolution, ExtractionSelection, extractionPreview, confirmedExtractionEdit } from './extractGrammarFlow';
import { ExtractionReview } from './extractionReview';

export function registerGrammarExtraction(context: ExtensionContext, client: LanguageClient): void {
  const previews = new Map<string, string>();
  let previewId = 0;
  let running = false;
  const applyButton = window.createStatusBarItem('nitrogen.applyExtraction', StatusBarAlignment.Left, 1001);
  applyButton.name = 'Apply grammar extraction';
  applyButton.text = '$(check) Apply all changes';
  applyButton.command = 'nitrogen.applyGrammarExtraction';
  const cancelButton = window.createStatusBarItem('nitrogen.cancelExtraction', StatusBarAlignment.Left, 1000);
  cancelButton.name = 'Cancel grammar extraction';
  cancelButton.text = '$(close) Cancel extraction';
  cancelButton.command = 'nitrogen.cancelGrammarExtraction';
  cancelButton.tooltip = 'Cancel the complete extraction without applying changes';
  const review = new ExtractionReview(pending => {
    void commands.executeCommand('setContext', 'nitrogen.extractionReviewPending', pending);
    if (pending) { applyButton.show(); cancelButton.show(); }
    else { applyButton.hide(); cancelButton.hide(); }
  });
  context.subscriptions.push(review, applyButton, cancelButton,
    commands.registerCommand('nitrogen.applyGrammarExtraction', () => review.accept()),
    commands.registerCommand('nitrogen.cancelGrammarExtraction', () => review.cancel()));
  context.subscriptions.push(workspace.registerTextDocumentContentProvider('nitrogen-extraction', {
    provideTextDocumentContent: uri => previews.get(uri.toString()) ?? '',
  }));
  const runExtraction = async (helper: boolean, argument?: ExtractionSelection): Promise<void> => {
    const command = helper ? 'nitrogen.extractQuoteHelper' : 'nitrogen.extractGrammar';
    const title = helper ? 'Extract Quote Helper' : 'Extract Block';
    if (running) {
      void window.showWarningMessage('An extraction is already in progress. Apply or cancel the current review first.');
      return;
    }
    const editor = window.activeTextEditor;
    const selection = argument ?? (editor && {
      textDocument: { uri: editor.document.uri.toString() },
      range: { start: editor.selection.start, end: editor.selection.end },
    });
    if (!selection) { void window.showWarningMessage('Select complete grammar definitions first.'); return; }
    const warn = (message: string): void => {
      void window.showWarningMessage(message, 'Retry').then(async choice => {
        if (choice !== 'Retry') return;
        // Restore the source's current editor selection, never the stale proposal range.
        await window.showTextDocument(Uri.parse(selection.textDocument.uri), { preview: false });
        await commands.executeCommand(command);
      }).then(undefined, error => {
        void window.showWarningMessage('Could not retry extraction: ' + String(error));
      });
    };
    const host: ExtractionHost = {
      prepare: value => client.sendRequest<ExtractionPreparation>('nitrogen/prepareGrammarExtraction', value),
      destination: async fileName => {
        const source = Uri.parse(selection.textDocument.uri);
        const selected = await window.showSaveDialog({
          title: `${title} — destination grammar`, saveLabel: 'Choose destination',
          defaultUri: source.with({ path: path.posix.join(path.posix.dirname(source.path), fileName) }),
          filters: { 'Nitrogen grammar': ['ngr'] },
        });
        return selected?.toString();
      },
      moduleName: suggested => Promise.resolve(window.showInputBox({
        title: `${title} — module name`, value: suggested, prompt: 'Name of the reusable syntax module',
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
        await commands.executeCommand('vscode.changes', `${title} — review all changes`, diffs);
        applyButton.text = '$(check) Apply all changes';
        applyButton.command = 'nitrogen.applyGrammarExtraction';
        applyButton.tooltip = `Apply the complete extraction to ${changes.length} files. ${result.notices.join(' ')}`;
        const confirmed = await review.begin();
        if (confirmed) {
          applyButton.text = '$(sync~spin) Applying extraction…';
          applyButton.command = undefined;
          applyButton.tooltip = 'Validating and applying the complete reviewed extraction';
          applyButton.show();
        }
        return confirmed;
      },
      apply: async result => {
        const converted = await client.protocol2CodeConverter.asWorkspaceEdit(confirmedExtractionEdit(result));
        // VS Code's public WorkspaceEdit does not carry LSP version numbers. Check the server's
        // snapshots after review, then apply the complete edit in this same event-loop turn.
        const documents = await Promise.all(result.expectedDocuments.map(expected => workspace.openTextDocument(Uri.parse(expected.uri))));
        for (let i = 0; i < documents.length; i++) {
          const expected = result.expectedDocuments[i], actual = documents[i];
          if (actual.getText() !== expected.text || (expected.version != null && actual.version !== expected.version)) {
            warn(`File '${workspace.asRelativePath(Uri.parse(expected.uri))}' changed after review. Select the definitions again.`);
            return false;
          }
        }
        const applied = await workspace.applyEdit(converted);
        if (!applied) warn('Extraction could not be applied. Review the files before retrying.');
        return applied;
      },
      open: async uri => { await window.showTextDocument(Uri.parse(uri), { preview: false }); },
      complete: (result, destination) => {
        const files = extractionPreview(result).map(change => workspace.asRelativePath(Uri.parse(change.uri)));
        void window.showInformationMessage(
          `Extracted to ${workspace.asRelativePath(Uri.parse(destination))}. Changed ${files.length} files: ${files.join(', ')}. Save All to persist these changes.`);
      },
      warn,
    };
    running = true;
    try {
      if (!helper) await extractGrammar(host, selection);
      else await extractQuoteHelper({ ...host,
        prepare: value => client.sendRequest<QuoteHelperPreparation>('nitrogen/prepareQuoteHelperExtraction', value),
        resolve: request => client.sendRequest<ExtractionResolution>('nitrogen/resolveQuoteHelperExtraction', request),
        helperInputs: async prepared => {
          const helperName = await window.showInputBox({ title: 'Extract Quote Helper — name',
            value: prepared.suggestedHelperName, prompt: 'Name of the reusable quote helper',
            validateInput: value => /^[A-Za-z_]\w*$/.test(value) ? undefined : 'Use a helper name such as FortnightDuration.',
          });
          if (helperName === undefined) return;
          const kind = await window.showQuickPick(['Current module', 'Existing module', 'New module'],
            { title: 'Extract Quote Helper — destination', placeHolder: 'Choose where to declare the helper' });
          if (!kind) return;
          if (kind === 'Current module') return { helperName, destinationUri: prepared.sourceUri!, module: prepared.sourceModule!, existingDestination: true };
          if (kind === 'Existing module') {
            const target = await window.showQuickPick((prepared.destinations ?? []).map(d => ({
              label: d.module, description: workspace.asRelativePath(Uri.parse(d.uri)), target: d,
            })), { title: 'Extract Quote Helper — existing module', matchOnDescription: true });
            return target && { helperName, destinationUri: target.target.uri, module: target.target.module, existingDestination: true };
          }
          const destinationUri = await host.destination('Shared.ngr');
          if (!destinationUri) return;
          const module = await host.moduleName('Shared');
          if (module === undefined) return;
          return { helperName, destinationUri, module, existingDestination: false };
        },
      }, selection);
    }
    finally { running = false; review.cancel(); applyButton.hide(); }
  };
  context.subscriptions.push(
    commands.registerCommand('nitrogen.extractGrammar', argument => runExtraction(false, argument)),
    commands.registerCommand('nitrogen.extractQuoteHelper', argument => runExtraction(true, argument)));
}
