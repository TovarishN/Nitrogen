import * as assert from 'node:assert/strict';
import * as path from 'node:path';
import { test } from 'node:test';
import { serverCommand, ServerInputs } from './server';

const extension = path.join(path.sep, 'ext');
const dll = path.join(extension, 'server', 'nitrogen.dll');

function inputs(overrides: Partial<ServerInputs>, files: string[] = [dll]): ServerInputs {
  return { serverPath: '', dotnetPath: '', extensionPath: extension, env: {}, platform: 'linux', exists: file => files.includes(file), ...overrides };
}

test('a server path set in the settings wins', () => {
  assert.deepEqual(serverCommand(inputs({ serverPath: '/opt/nitrogen' })), { command: '/opt/nitrogen', args: ['lsp'] });
});

test('the bundled server runs on the dotnet setting', () => {
  assert.deepEqual(serverCommand(inputs({ dotnetPath: '/x/dotnet' })), { command: '/x/dotnet', args: [dll, 'lsp'] });
});

test('dotnet is found through DOTNET_ROOT, the standard location, then PATH', () => {
  assert.deepEqual(serverCommand(inputs({ env: { DOTNET_ROOT: '/r' } }, [dll, '/r/dotnet'])), { command: '/r/dotnet', args: [dll, 'lsp'] });
  assert.deepEqual(serverCommand(inputs({}, [dll, '/usr/share/dotnet/dotnet'])), { command: '/usr/share/dotnet/dotnet', args: [dll, 'lsp'] });
  assert.deepEqual(serverCommand(inputs({ env: { PATH: '/a:/b' } }, [dll, '/b/dotnet'])), { command: '/b/dotnet', args: [dll, 'lsp'] });
  assert.deepEqual(serverCommand(inputs({ platform: 'darwin' }, [dll, '/usr/local/share/dotnet/dotnet'])),
    { command: '/usr/local/share/dotnet/dotnet', args: [dll, 'lsp'] });
  assert.deepEqual(serverCommand(inputs({ platform: 'win32', env: { ProgramFiles: 'C:\\PF' } }, [dll, 'C:\\PF\\dotnet\\dotnet.exe'])),
    { command: 'C:\\PF\\dotnet\\dotnet.exe', args: [dll, 'lsp'] });
});

test('without dotnet the bundled server cannot start', () => {
  const result = serverCommand(inputs({ env: { PATH: '/a' } }));
  assert.ok('error' in result);
  assert.match(result.error, /\.NET 10/);
  assert.match(result.error, /nitrogen\.dotnetPath/);
});

test('without a bundled server, nitrogen runs from PATH', () => {
  assert.deepEqual(serverCommand(inputs({}, [])), { command: 'nitrogen', args: ['lsp'] });
});
