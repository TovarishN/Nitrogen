import * as assert from 'node:assert/strict';
import { test } from 'node:test';
import { nitrideInstalled } from './nitride';

test('nitride installed: this extension stays inactive', () => {
  assert.equal(nitrideInstalled(id => (id === 'nitride.nitride' ? {} : undefined)), true);
});

test('nitride absent: this extension serves', () => {
  assert.equal(nitrideInstalled(() => undefined), false);
});
