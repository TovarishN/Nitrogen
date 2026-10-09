import * as path from 'path';

/** What choosing the server depends on: the settings, the extension's folder, the environment and the file system. */
export interface ServerInputs {
  /** nitrogen.server.path */
  serverPath: string;
  /** nitrogen.dotnetPath */
  dotnetPath: string;
  extensionPath: string;
  env: Record<string, string | undefined>;
  platform: string;
  exists: (file: string) => boolean;
}

export type ServerCommand = { command: string; args: string[] } | { error: string };

/**
 * The server to start: the executable set in the settings; else the server bundled in server/, on the
 * dotnet host; else nitrogen from PATH (an extension built from source, without a bundled server).
 */
export function serverCommand(inputs: ServerInputs): ServerCommand {
  if (inputs.serverPath) return { command: inputs.serverPath, args: ['lsp'] };
  const dll = path.join(inputs.extensionPath, 'server', 'nitrogen.dll');
  if (!inputs.exists(dll)) return { command: 'nitrogen', args: ['lsp'] };
  const dotnet = findDotnet(inputs);
  return dotnet
    ? { command: dotnet, args: [dll, 'lsp'] }
    : { error: 'Nitrogen needs the .NET 10 runtime: install it, or set nitrogen.dotnetPath or nitrogen.server.path.' };
}

/**
 * The dotnet host: the setting, then DOTNET_ROOT, then the standard install locations, then PATH.
 * The extensions `nitrogen package` generates look in the same places (VsCodeRenderer).
 */
function findDotnet(inputs: ServerInputs): string | undefined {
  if (inputs.dotnetPath) return inputs.dotnetPath;
  const windows = inputs.platform === 'win32';
  const paths = windows ? path.win32 : path.posix;
  const exe = windows ? 'dotnet.exe' : 'dotnet';
  const candidates: string[] = [];
  if (inputs.env.DOTNET_ROOT) candidates.push(paths.join(inputs.env.DOTNET_ROOT, exe));
  if (windows) candidates.push(paths.join(inputs.env.ProgramFiles ?? 'C:\\Program Files', 'dotnet', exe));
  if (inputs.platform === 'darwin') candidates.push('/usr/local/share/dotnet/dotnet');
  if (inputs.platform === 'linux') candidates.push('/usr/share/dotnet/dotnet', '/usr/lib/dotnet/dotnet');
  for (const directory of (inputs.env.PATH ?? inputs.env.Path ?? '').split(windows ? ';' : ':'))
    if (directory) candidates.push(paths.join(directory, exe));
  return candidates.find(inputs.exists);
}
