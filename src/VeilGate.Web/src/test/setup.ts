// Node 25 has its own experimental localStorage that hides the one jsdom provides and does not work without a storage file.
// Tests get a small in-memory one instead, so they behave the same on every Node version.
// (Defined directly rather than with vi.stubGlobal, because the theme tests call vi.unstubAllGlobals().)
class MemoryStorage {
  private data = new Map<string, string>();

  get length() {
    return this.data.size;
  }
  clear() {
    this.data.clear();
  }
  getItem(key: string) {
    return this.data.get(key) ?? null;
  }
  key(index: number) {
    return [...this.data.keys()][index] ?? null;
  }
  removeItem(key: string) {
    this.data.delete(key);
  }
  setItem(key: string, value: string) {
    this.data.set(key, String(value));
  }
}

Object.defineProperty(globalThis, "localStorage", {
  value: new MemoryStorage(),
  configurable: true,
  writable: true,
});
