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
    path: "/dashboard",
    title: "Dashboard",
    summary: "A one-screen picture of how the gateway is doing.",
    planned: [
      "How many requests were handled, and how many sensitive items were protected",
      "Policy changes waiting for approval",
      "Recent evaluation results",
    ],
  },
  {
    path: "/requests",
    title: "Requests",
    summary: "A searchable history of what went through the gateway.",
    planned: [
      "When each request happened, which client sent it and which policy applied",
      "How many items were protected in each, never the text itself",
      "Filters by client, policy and time",
    ],
  },
  {
    path: "/users",
    title: "Users",
    summary: "The people in your organisation and what they may do.",
    planned: [
      "Invite people by email and set their role",
      "Disable an account and reset a person's multi-factor setup",
      "See who signed in recently",
    ],
  },
  {
    path: "/security",
    title: "Security",
    summary: "Sign-in activity and protections across the organisation.",
    planned: [
      "Active sessions, with the option to end them",
      "Who has multi-factor sign-in set up",
      "Alerts for repeated failed sign-ins",
    ],
  },
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
