import {
  Activity,
  ClipboardCheck,
  FlaskConical,
  KeyRound,
  LayoutDashboard,
  Lock,
  ScrollText,
  Server,
  Settings,
  ShieldCheck,
  Users,
  type LucideIcon,
} from "lucide-react";
import { ALL_TENANT_ROLES } from "./roles";

export type NavItem = {
  label: string;
  path: string;
  icon: LucideIcon;
  roles: readonly string[];
};

const ADMIN = ["TenantAdmin"];
const BUILDERS = ["TenantAdmin", "Developer"];
const OVERSIGHT = ["TenantAdmin", "SecurityOfficer"];

// Which role sees which link. This only decides what to show; the server checks every request.
export const NAV_ITEMS: readonly NavItem[] = [
  {
    label: "Dashboard",
    path: "/dashboard",
    icon: LayoutDashboard,
    roles: ALL_TENANT_ROLES,
  },
  {
    label: "Playground",
    path: "/playground",
    icon: FlaskConical,
    roles: ["TenantAdmin", "SecurityOfficer", "Developer"],
  },
  {
    label: "Policies",
    path: "/policies",
    icon: ShieldCheck,
    roles: ALL_TENANT_ROLES,
  },
  { label: "API clients", path: "/clients", icon: KeyRound, roles: BUILDERS },
  { label: "Providers", path: "/providers", icon: Server, roles: BUILDERS },
  {
    label: "Evaluations",
    path: "/evaluations",
    icon: ClipboardCheck,
    roles: ALL_TENANT_ROLES,
  },
  {
    label: "Requests",
    path: "/requests",
    icon: Activity,
    roles: ALL_TENANT_ROLES,
  },
  {
    label: "Audit log",
    path: "/audit",
    icon: ScrollText,
    roles: ["TenantAdmin", "SecurityOfficer", "Auditor"],
  },
  { label: "Users", path: "/users", icon: Users, roles: ADMIN },
  { label: "Security", path: "/security", icon: Lock, roles: OVERSIGHT },
  { label: "Settings", path: "/settings", icon: Settings, roles: ADMIN },
];

export const navFor = (role: string): NavItem[] =>
  NAV_ITEMS.filter((item) => item.roles.includes(role));
