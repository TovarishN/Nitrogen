/** One whole-proposal decision, independent of transient notification visibility. */
export class ExtractionReview {
  private decision?: (apply: boolean) => void;
  private disposed = false;

  constructor(private readonly changed: (pending: boolean) => void) {}

  get pending(): boolean { return this.decision !== undefined; }

  begin(): Promise<boolean> {
    if (this.disposed) throw Error('Extraction review is disposed.');
    if (this.pending) throw Error('An extraction review is already pending.');
    const result = new Promise<boolean>(resolve => { this.decision = resolve; });
    this.changed(true);
    return result;
  }

  accept(): void { this.finish(true); }
  cancel(): void { this.finish(false); }
  dispose(): void { this.cancel(); this.disposed = true; }

  private finish(apply: boolean): void {
    const decision = this.decision;
    if (!decision) return;
    this.decision = undefined;
    this.changed(false);
    decision(apply);
  }
}
