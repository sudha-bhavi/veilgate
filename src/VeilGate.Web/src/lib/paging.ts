export const pageCount = (total: number, pageSize: number): number =>
  Math.max(1, Math.ceil(total / pageSize));

/** Reads a ?page= value from the address bar; anything odd means page 1. */
export function readPage(value: string | null): number {
  const page = Number(value);
  return Number.isInteger(page) && page >= 1 ? page : 1;
}
