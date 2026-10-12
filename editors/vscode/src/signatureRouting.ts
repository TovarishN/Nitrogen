import { Disposable, ExtensionContext, languages, TextDocument, workspace } from 'vscode';
import { Middleware } from 'vscode-languageclient/node';
import { signatureDocumentMode, signatureLanguageId } from './signatureDocuments';

/** A single client owns signed documents; native augmentations retain their original language. */
export class SignatureRouting implements Disposable {
  private readonly originals = new Map<string, string>();
  private readonly changing = new Set<string>();
  private readonly listeners: Disposable[] = [];
  constructor(private readonly owner: boolean) {}

  start(context: ExtensionContext): void {
    if (!this.owner) return;
    this.listeners.push(workspace.onDidOpenTextDocument(document => void this.classify(document)),
      workspace.onDidChangeTextDocument(event => void this.classify(event.document)),
      workspace.onDidCloseTextDocument(document => {
        if (!this.changing.has(document.uri.toString())) this.originals.delete(document.uri.toString());
      }));
    context.subscriptions.push(this);
    for (const document of workspace.textDocuments) void this.classify(document);
  }

  private async classify(document: TextDocument): Promise<void> {
    if (document.uri.scheme !== 'file') return;
    const key = document.uri.toString();
    if (this.changing.has(key)) return;
    const mode = signatureDocumentMode(document.getText());
    const original = this.originals.get(key) ?? document.languageId;
    const target = signatureLanguageId(mode, original);
    if (target === document.languageId) return;
    this.originals.set(key, original);
    this.changing.add(key);
    try { await languages.setTextDocumentLanguage(document, target); }
    finally { this.changing.delete(key); }
    // An edit may arrive while the language transition is in flight.
    const current = workspace.textDocuments.find(d => d.uri.toString() === key);
    if (current && !current.isClosed && signatureLanguageId(signatureDocumentMode(current.getText()), original) !== current.languageId)
      void this.classify(current);
  }

  middleware(base: Middleware, primary: (document: TextDocument) => boolean): Middleware {
    return new Proxy(base, {
      get: (target, property, receiver) => {
        const existing = Reflect.get(target, property, receiver);
        if (typeof property !== 'string' || (!property.startsWith('provide') && property !== 'prepareRename')) return existing;
        return (...args: unknown[]) => {
          const document = args[0] as TextDocument | undefined;
          if (document && typeof document.getText === 'function') {
            const mode = signatureDocumentMode(document.getText());
            if ((mode && !this.owner) || mode === 'augment' || (!mode && !primary(document))) return null;
          }
          if (typeof existing === 'function') return existing(...args);
          const next = args.pop() as (...parameters: unknown[]) => unknown;
          return next(...args);
        };
      },
    });
  }

  dispose(): void { for (const listener of this.listeners) listener.dispose(); }
}
