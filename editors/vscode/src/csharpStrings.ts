import { Disposable, Range, SemanticTokens, TextDocument, TextEditorDecorationType, ThemeColor, Uri, window, workspace } from 'vscode';
import { DocumentSelector, LanguageClient, Middleware } from 'vscode-languageclient/node';

/**
 * Languages inside tagged C# strings (`/*lang=calc*\/ "1 + 2;"`, `// language=calc`). The Nitrogen server
 * serves C# documents only within those strings. VS Code shows one semantic-token provider per document,
 * so the C# extension keeps colouring the file and the server's tokens are painted as decorations. Rename
 * and outline stay with C#; the server answers rename only inside a tagged string.
 */
export const csharpSelector: DocumentSelector = [{ scheme: 'file', language: 'csharp' }];

/** Theme colours for the server's token types (the legend's names). */
const colours: Record<string, string> = {
  namespace: 'symbolIcon.namespaceForeground',
  type: 'symbolIcon.classForeground',
  class: 'symbolIcon.classForeground',
  enum: 'symbolIcon.enumeratorForeground',
  interface: 'symbolIcon.interfaceForeground',
  struct: 'symbolIcon.structForeground',
  typeParameter: 'symbolIcon.typeParameterForeground',
  parameter: 'symbolIcon.variableForeground',
  variable: 'symbolIcon.variableForeground',
  property: 'symbolIcon.propertyForeground',
  enumMember: 'symbolIcon.enumeratorMemberForeground',
  event: 'symbolIcon.eventForeground',
  function: 'symbolIcon.functionForeground',
  method: 'symbolIcon.methodForeground',
  macro: 'symbolIcon.constantForeground',
  keyword: 'symbolIcon.keywordForeground',
  modifier: 'symbolIcon.keywordForeground',
  comment: 'descriptionForeground',
  string: 'symbolIcon.stringForeground',
  number: 'symbolIcon.numberForeground',
  regexp: 'symbolIcon.stringForeground',
  operator: 'symbolIcon.operatorForeground',
};

const isCSharp = (document: TextDocument): boolean => document.languageId === 'csharp';

export class CSharpStrings implements Disposable {
  private readonly decorations = new Map<string, TextEditorDecorationType>();
  private readonly painted = new Map<string, Map<string, Range[]>>();
  private readonly listeners = [
    window.onDidChangeVisibleTextEditors(() => this.repaint()),
    workspace.onDidCloseTextDocument(document => this.painted.delete(document.uri.toString())),
  ];

  constructor(private readonly client: () => LanguageClient | undefined) {}

  /** Paints C# documents' tokens and keeps them from replacing C#'s; leaves rename and outline to C# outside the strings. */
  readonly middleware: Middleware = {
    provideDocumentSemanticTokens: async (document, token, next) => {
      const tokens = await next(document, token);
      if (!isCSharp(document)) return tokens;
      this.paint(document, tokens ?? undefined);
      return null;
    },
    provideDocumentSymbols: (document, token, next) => (isCSharp(document) ? [] : next(document, token)),
    prepareRename: async (document, position, token, next) => {
      try {
        return await next(document, position, token);
      } catch (error) {
        if (isCSharp(document)) return null;
        throw error;
      }
    },
    provideRenameEdits: async (document, position, newName, token, next) => {
      try {
        return await next(document, position, newName, token);
      } catch (error) {
        if (isCSharp(document)) return null;
        throw error;
      }
    },
  };

  private paint(document: TextDocument, tokens: SemanticTokens | undefined): void {
    const legend = this.client()?.initializeResult?.capabilities.semanticTokensProvider?.legend.tokenTypes ?? [];
    const byType = new Map<string, Range[]>();
    const data = tokens?.data ?? new Uint32Array();
    let line = 0;
    let character = 0;
    for (let i = 0; i + 4 < data.length; i += 5) {
      line += data[i];
      character = data[i] === 0 ? character + data[i + 1] : data[i + 1];
      const type = legend[data[i + 3]];
      if (!type || !colours[type]) continue;
      const ranges = byType.get(type) ?? [];
      ranges.push(new Range(line, character, line, character + data[i + 2]));
      byType.set(type, ranges);
    }
    this.painted.set(document.uri.toString(), byType);
    this.repaint(document.uri);
  }

  private repaint(only?: Uri): void {
    for (const editor of window.visibleTextEditors) {
      const key = editor.document.uri.toString();
      if (only && key !== only.toString()) continue;
      const byType = this.painted.get(key);
      if (!byType) continue;
      for (const [type, colour] of Object.entries(colours)) {
        let decoration = this.decorations.get(type);
        if (!decoration && byType.has(type)) {
          decoration = window.createTextEditorDecorationType({ color: new ThemeColor(colour) });
          this.decorations.set(type, decoration);
        }
        if (decoration) editor.setDecorations(decoration, byType.get(type) ?? []);
      }
    }
  }

  dispose(): void {
    for (const listener of this.listeners) listener.dispose();
    for (const decoration of this.decorations.values()) decoration.dispose();
    this.decorations.clear();
    this.painted.clear();
  }
}
