/**
 * Nitride (the native engine's extension) serves the same files and takes priority: while it is
 * installed, Nitrogen's extensions stay inactive. Uninstall Nitride to use Nitrogen again.
 */
export function nitrideInstalled(getExtension: (id: string) => unknown): boolean {
  return getExtension('nitride.nitride') !== undefined;
}
