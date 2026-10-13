import * as assert from 'node:assert/strict';
import { test } from 'node:test';
import { ExtractionReview } from './extractionReview';

test('review remains pending until Apply and resolves only once', async () => {
  const states: boolean[] = [];
  const review = new ExtractionReview(pending => states.push(pending));
  const decision = review.begin();
  assert.equal(review.pending, true);
  review.accept();
  review.cancel();
  assert.equal(await decision, true);
  assert.equal(review.pending, false);
  assert.deepEqual(states, [true, false]);
});

test('Cancel and extension disposal both discard a pending review', async () => {
  const review = new ExtractionReview(() => {});
  const cancelled = review.begin();
  review.cancel();
  assert.equal(await cancelled, false);
  const disposed = review.begin();
  review.dispose();
  assert.equal(await disposed, false);
  assert.equal(review.pending, false);
});

test('a second review cannot replace the proposal awaiting a decision', async () => {
  const review = new ExtractionReview(() => {});
  const first = review.begin();
  assert.throws(() => review.begin(), /already/);
  review.accept();
  assert.equal(await first, true);
});
