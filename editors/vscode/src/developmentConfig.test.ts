import * as assert from 'node:assert/strict';
import * as path from 'node:path';
import { test } from 'node:test';
import { languageConfig } from './developmentConfig';

const bundled = path.resolve('bundle/language/nitrogen.json');
const root = path.resolve('workspace');
const config = path.join(root, 'nitrogen.json');

test('normal mode uses the bundled grammar even in a language workspace', () => {
  assert.deepEqual(languageConfig(bundled, false, [root], () => true), { config: bundled });
});

test('development mode uses the workspace config instead of the bundle', () => {
  assert.deepEqual(languageConfig(bundled, true, [root], file => file === config), { config });
});

test('development mode finds the single configured folder in a multi-root workspace', () => {
  assert.deepEqual(languageConfig(bundled, true, [path.resolve('other'), root], file => file === config), { config });
});

test('development mode reports a missing config instead of falling back to a stale bundle', () => {
  assert.match((languageConfig(bundled, true, [], () => false) as { error: string }).error, /nitrogen.json/);
  assert.match((languageConfig(bundled, true, [root], () => false) as { error: string }).error, /nitrogen.json/);
});

test('development mode rejects ambiguous workspace configurations', () => {
  assert.match((languageConfig(bundled, true, [root, path.resolve('other')], () => true) as { error: string }).error,
    /multiple.*nitrogen.json/);
});
