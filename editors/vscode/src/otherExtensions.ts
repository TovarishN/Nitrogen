import * as fs from 'fs';
import * as path from 'path';

/** The part of an installed extension this module reads (VS Code's `Extension` has both). */
export interface InstalledExtension {
  readonly id: string;
  readonly extensionPath: string;
}

/** The language names and extensions (without the dot) a nitrogen.json declares; none when it does not parse. */
export function languagesIn(json: string): string[] {
  try {
    const config = JSON.parse(json) as { languages?: { name?: string; extensions?: string[] }[] };
    return (config.languages ?? []).flatMap(language => [
      ...(language.name ? [language.name] : []),
      ...(language.extensions ?? []).map(extension => extension.replace(/^\./, '')),
    ]);
  } catch {
    return [];
  }
}

/**
 * The languages other installed Nitrogen extensions carry in their bundle (generated plugins: `nitrogen.nitrogen-*`
 * with bundle/language/nitrogen.json). A server that reads the workspace's nitrogen.json leaves their tagged C#
 * strings to them (`skipLanguages`), so those strings are not served twice.
 */
export function languagesOfOtherExtensions(all: readonly InstalledExtension[], ownId: string): string[] {
  const languages = all
    .filter(extension => extension.id.toLowerCase() !== ownId.toLowerCase() && extension.id.toLowerCase().startsWith('nitrogen.nitrogen-'))
    .flatMap(extension => {
      const config = path.join(extension.extensionPath, 'bundle', 'language', 'nitrogen.json');
      return fs.existsSync(config) ? languagesIn(fs.readFileSync(config, 'utf8')) : [];
    });
  return [...new Set(languages)];
}
