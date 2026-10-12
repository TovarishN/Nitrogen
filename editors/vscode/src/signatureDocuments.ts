export type SignatureDocumentMode = 'grammar' | 'language' | 'augment';

/** Activation hint only. The server validates syntax, references, and host comments. */
export function signatureDocumentMode(text: string): SignatureDocumentMode | undefined {
  let rest = text.replace(/^\uFEFF/, '');
  let augmentation = false;
  while (rest.length) {
    rest = rest.trimStart();
    if (rest.startsWith('#!nitrogen')) {
      const mode = /^#!nitrogen\s+(grammar|language|augment)\b/.exec(rest)?.[1] as SignatureDocumentMode | undefined;
      if (mode === 'grammar' || mode === 'language') return mode;
      if (mode === 'augment') augmentation = true;
      const end = rest.search(/[\r\n]/);
      rest = end < 0 ? '' : rest.slice(end + 1);
    } else if (rest.startsWith('//')) {
      const end = rest.search(/[\r\n]/);
      const line = end < 0 ? rest : rest.slice(0, end);
      const mode = signatureDocumentMode(line.slice(2));
      if (mode && mode !== 'augment') return mode;
      augmentation ||= mode === 'augment';
      rest = end < 0 ? '' : rest.slice(end + 1);
    } else if (rest.startsWith('/*') || rest.startsWith('<!--')) {
      const html = rest.startsWith('<!--');
      const end = rest.indexOf(html ? '-->' : '*/');
      if (end < 0) break;
      const modes = rest.slice(html ? 4 : 2, end).split(/\r\n|\r|\n/)
        .map(line => /^\s*#!nitrogen\s+(grammar|language|augment)\b/.exec(line)?.[1] as SignatureDocumentMode | undefined);
      const mode = modes.find(value => value === 'grammar' || value === 'language') ?? modes.find(value => value === 'augment');
      if (mode && mode !== 'augment') return mode;
      augmentation ||= mode === 'augment';
      rest = rest.slice(end + (html ? 3 : 2));
    } else break;
  }
  return augmentation ? 'augment' : undefined;
}

export function signatureLanguageId(mode: SignatureDocumentMode | undefined, original: string): string {
  return mode === 'grammar' ? 'ngr' : mode === 'language' ? 'nitrogen-signature' : original;
}

export function ownsSignatureDocuments(ownId: string, installed: readonly string[]): boolean {
  const owners = installed.map(id => id.toLowerCase()).filter(id => id === 'nitrogen.nitrogen' || id.startsWith('nitrogen.nitrogen-')).sort();
  return ownId.toLowerCase() === (owners.includes('nitrogen.nitrogen') ? 'nitrogen.nitrogen' : owners[0]);
}
