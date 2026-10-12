import * as path from 'path';

/** A generated plugin normally uses its bundle; development mode explicitly selects a workspace grammar. */
export function languageConfig(
  bundledConfig: string,
  developmentMode: boolean,
  workspaceRoots: readonly string[],
  exists: (file: string) => boolean,
): { config: string } | { error: string } {
  if (!developmentMode) return { config: bundledConfig };
  const configs = [...new Set(workspaceRoots.map(root => path.join(root, 'nitrogen.json')).filter(exists))];
  if (configs.length === 1) return { config: configs[0] };
  return { error: configs.length === 0
    ? 'Development mode requires nitrogen.json at a workspace folder root. Open your language project folder.'
    : 'Development mode found multiple workspace nitrogen.json files. Open the language project in its own window.' };
}
