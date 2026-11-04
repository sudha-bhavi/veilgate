export type ComingSoon = {
  /** Must match a link in the sidebar (src/auth/nav.ts). Who may open it comes from there too. */
  path: string;
  title: string;
  summary: string;
  planned: string[];
};

// Sections that are in the plan but whose server side is not built yet. Each page says so plainly instead of pretending.
export const COMING_SOON: readonly ComingSoon[] = [
  {
    path: "/settings",
    title: "Settings",
    summary: "Organisation-wide choices.",
    planned: [
      "The organisation's name and default policy",
      "How long to keep request history",
      "Sign-in and idle timeout rules",
    ],
  },
];
