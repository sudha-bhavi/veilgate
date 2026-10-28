export type Theme = "dark" | "light";

const KEY = "theme"; // localStorage holds only this and the sidebar state (doc 02 §8.2): nothing sensitive

function stored(): Theme | null {
  try {
    const value = localStorage.getItem(KEY);
    return value === "dark" || value === "light" ? value : null;
  } catch {
    return null; // storage can be blocked; the app must still work
  }
}

/** The saved choice, else the operating system's preference, else dark. */
export function currentTheme(): Theme {
  const saved = stored();
  if (saved) return saved;
  return window.matchMedia?.("(prefers-color-scheme: light)").matches
    ? "light"
    : "dark";
}

export function applyTheme(theme: Theme): void {
  document.documentElement.classList.toggle("dark", theme === "dark");
}

export function setTheme(theme: Theme): void {
  try {
    localStorage.setItem(KEY, theme);
  } catch {
    // not remembered, but still applied for this visit
  }
  applyTheme(theme);
}
