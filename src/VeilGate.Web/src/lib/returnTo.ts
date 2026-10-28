/** Only a path inside this app is accepted; anything else falls back to the dashboard. */
export function safeReturnTo(
  value: string | null | undefined,
  fallback = "/dashboard",
): string {
  if (
    !value ||
    !value.startsWith("/") ||
    value.startsWith("//") ||
    value.includes("\\")
  )
    return fallback;
  if ([...value].some((ch) => ch.charCodeAt(0) < 32)) return fallback;
  return value;
}
