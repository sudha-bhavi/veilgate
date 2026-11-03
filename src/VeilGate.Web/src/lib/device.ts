/**
 * Turns a browser's own description (the User-Agent text) into something a person recognises, like "Chrome on macOS".
 * This is only a label for a list, so it looks for the usual markers and says "Unknown device" when it finds none.
 */
export function describeDevice(userAgent: string | null | undefined): string {
  if (!userAgent) return "Unknown device";
  const browser = browserOf(userAgent);
  const system = systemOf(userAgent);
  if (browser && system) return `${browser} on ${system}`;
  return browser ?? system ?? "Unknown device";
}

// Order matters: Edge and Opera also say "Chrome", and Chrome also says "Safari".
function browserOf(ua: string): string | null {
  if (/\bEdgA?\/|\bEdgiOS\//.test(ua)) return "Edge";
  if (/\bOPR\/|\bOpera\b/.test(ua)) return "Opera";
  if (/\bFirefox\/|\bFxiOS\//.test(ua)) return "Firefox";
  if (/\bChrome\/|\bCriOS\//.test(ua)) return "Chrome";
  if (/\bSafari\//.test(ua)) return "Safari";
  return null;
}

// iPhones and iPads say "like Mac OS X", so they are checked before macOS.
function systemOf(ua: string): string | null {
  if (/\biPhone\b/.test(ua)) return "iPhone";
  if (/\biPad\b/.test(ua)) return "iPad";
  if (/\bAndroid\b/.test(ua)) return "Android";
  if (/\bWindows\b/.test(ua)) return "Windows";
  if (/\bCrOS\b/.test(ua)) return "ChromeOS";
  if (/\bMac OS X\b|\bMacintosh\b/.test(ua)) return "macOS";
  if (/\bLinux\b/.test(ua)) return "Linux";
  return null;
}
