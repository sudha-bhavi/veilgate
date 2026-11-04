export type ComingSoon = {
  /** Must match a link in the sidebar (src/auth/nav.ts). Who may open it comes from there too. */
  path: string;
  title: string;
  summary: string;
  planned: string[];
};

// Sections that are in the plan but whose server side is not built yet. Each page says so plainly instead of pretending.
// Empty for now: every sidebar section is built.
export const COMING_SOON: readonly ComingSoon[] = [];
