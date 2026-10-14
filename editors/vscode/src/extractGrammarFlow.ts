import type { WorkspaceEdit } from 'vscode-languageclient/node';

export interface ExtractionSelection {
  textDocument: { uri: string };
  range: { start: { line: number; character: number }; end: { line: number; character: number } };
}
export interface ExtractionPreparation {
  id?: string; suggestedModule?: string; suggestedFileName?: string; unavailableReason?: string;
}
export interface ExtractionResolution {
  action?: { edit?: WorkspaceEdit }; unavailableReason?: string; notices: readonly string[];
  expectedDocuments: readonly { uri: string; version?: number | null; text: string }[];
}
export interface ExtractionHost {
  prepare(selection: ExtractionSelection): Promise<ExtractionPreparation>;
  destination(fileName: string): Promise<string | undefined>;
  moduleName(suggested: string): Promise<string | undefined>;
  resolve(request: { id: string; destinationUri: string; module: string }): Promise<ExtractionResolution>;
  review(result: ExtractionResolution): Promise<boolean>;
  apply(result: ExtractionResolution): Promise<boolean>;
  open(uri: string): Promise<void>;
  complete?(result: ExtractionResolution, destinationUri: string): void;
  warn(message: string): void;
}

/** Prompt orchestration stays separate from editor APIs so cancellation and refusals are testable. */
export async function extractGrammar(host: ExtractionHost, selection: ExtractionSelection): Promise<void> {
  try {
    const prepared = await host.prepare(selection);
    if (!prepared.id) { host.warn(prepared.unavailableReason ?? 'Select complete grammar definitions.'); return; }
    const destinationUri = await host.destination(prepared.suggestedFileName ?? 'Extracted.ngr');
    if (!destinationUri) return;
    const module = await host.moduleName(prepared.suggestedModule ?? 'Extracted');
    if (module === undefined) return;
    if (!module.trim()) { host.warn('Choose a nonempty module name.'); return; }
    const resolved = await host.resolve({ id: prepared.id, destinationUri, module: module.trim() });
    await applyReviewedExtraction(host, resolved, () => host.resolve({ id: prepared.id!, destinationUri, module: module.trim() }), destinationUri);
  } catch (error) {
    host.warn('Extraction failed: ' + (error instanceof Error ? error.message : String(error)));
  }
}

export interface QuoteHelperPreparation {
  id?: string; suggestedHelperName?: string; sourceModule?: string; sourceUri?: string; unavailableReason?: string;
  destinations?: readonly { uri: string; module: string }[];
}
export interface QuoteHelperRequest {
  id: string; destinationUri: string; module: string; helperName: string; existingDestination: boolean;
}
export type QuoteHelperHost = Pick<ExtractionHost, 'review' | 'apply' | 'open' | 'complete' | 'warn'> & {
  prepare(selection: ExtractionSelection): Promise<QuoteHelperPreparation>;
  helperInputs(prepared: QuoteHelperPreparation): Promise<Omit<QuoteHelperRequest, 'id'> | undefined>;
  resolve(request: QuoteHelperRequest): Promise<ExtractionResolution>;
};
export async function extractQuoteHelper(host: QuoteHelperHost, selection: ExtractionSelection): Promise<void> {
  try {
    const prepared = await host.prepare(selection);
    if (!prepared.id) { host.warn(prepared.unavailableReason ?? 'Select a whole inline quote.'); return; }
    const inputs = await host.helperInputs(prepared);
    if (!inputs) return;
    const request = { id: prepared.id, ...inputs };
    const resolved = await host.resolve(request);
    await applyReviewedExtraction(host, resolved, () => host.resolve(request), inputs.destinationUri);
  } catch (error) {
    host.warn('Extraction failed: ' + (error instanceof Error ? error.message : String(error)));
  }
}
async function applyReviewedExtraction(host: Pick<ExtractionHost, 'review' | 'apply' | 'open' | 'complete' | 'warn'>,
  resolved: ExtractionResolution, resolve: () => Promise<ExtractionResolution>, destinationUri: string): Promise<void> {
    if (resolved.unavailableReason || !resolved.action?.edit) {
      host.warn(resolved.unavailableReason ?? 'Extraction is unavailable.'); return;
    }
    if (!await host.review(resolved)) return;
    const checked = await resolve();
    if (checked.unavailableReason || !checked.action?.edit) {
      host.warn(checked.unavailableReason ?? 'Extraction is unavailable.'); return;
    }
    if (reviewedEdit(checked.action.edit) !== reviewedEdit(resolved.action.edit)) {
      host.warn('The extraction changed during review. Select the definitions again.'); return;
    }
    if (await host.apply(checked)) {
      await host.open(destinationUri);
      host.complete?.(checked, destinationUri);
    }
}

/** Opening or closing an unchanged document during review may change its LSP version. */
function reviewedEdit(edit: WorkspaceEdit): string {
  return JSON.stringify({ ...edit, documentChanges: edit.documentChanges?.map(change =>
    'textDocument' in change ? { ...change, textDocument: { uri: change.textDocument.uri } } : change) });
}

export function extractionPreview(result: ExtractionResolution): { uri: string; before: string; after: string }[] {
  const previews = new Map<string, { uri: string; before: string; after: string }>();
  for (const change of result.action?.edit?.documentChanges ?? []) {
    if (!('textDocument' in change)) {
      if (change.kind !== 'create') throw Error('Unsupported extraction file operation.');
      previews.set(change.uri, { uri: change.uri, before: '', after: '' });
      continue;
    }
    const uri = change.textDocument.uri;
    const before = previews.get(uri)?.after ?? result.expectedDocuments.find(d => d.uri === uri)?.text;
    if (before === undefined) throw Error('Missing extraction document snapshot.');
    const offset = (position: { line: number; character: number }): number => {
      let start = 0;
      for (let line = 0; line < position.line; line++) start = before.indexOf('\n', start) + 1;
      return start + position.character;
    };
    let after = before;
    for (const edit of [...change.edits].sort((a, b) => offset(b.range.start) - offset(a.range.start)))
      after = after.slice(0, offset(edit.range.start)) + edit.newText + after.slice(offset(edit.range.end));
    previews.set(uri, { uri, before: previews.get(uri)?.before ?? before, after });
  }
  return [...previews.values()];
}

/** The entire proposal has already been reviewed. Never reopen a subset-selectable bulk edit. */
export function confirmedExtractionEdit(result: ExtractionResolution): WorkspaceEdit {
  return { documentChanges: result.action!.edit!.documentChanges!.map(change => {
    if ('textDocument' in change)
      return { textDocument: change.textDocument, edits: change.edits.map(edit => ({ range: edit.range, newText: edit.newText })) };
    const { annotationId: _, ...operation } = change;
    return operation;
  }) };
}
