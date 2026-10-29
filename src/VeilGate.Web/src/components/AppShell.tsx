import { useState } from "react";
import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { LogOut, Moon, Sun } from "lucide-react";
import { navFor } from "../auth/nav";
import { useIdleSignOut } from "../auth/useIdleSignOut";
import { useSession } from "../auth/useSession";
import { signOut } from "../lib/api";
import { currentTheme, setTheme, type Theme } from "../lib/theme";
import { showToast } from "../lib/toast";
import Button from "./Button";
import IdleWarning from "./IdleWarning";

// "TenantAdmin" -> "Tenant Admin"
const readable = (role: string) => role.replace(/([a-z])([A-Z])/g, "$1 $2");

export default function AppShell() {
  const session = useSession();
  const navigate = useNavigate();
  const [theme, setCurrent] = useState<Theme>(currentTheme);
  const [signingOut, setSigningOut] = useState(false);

  async function handleIdle() {
    showToast("You were signed out because of inactivity.", "info");
    await signOut();
    navigate("/login", { replace: true });
  }
  const { warning, stayActive } = useIdleSignOut(handleIdle);

  if (session.status !== "signedIn") return null; // RequireAuth already handles the other cases

  function toggleTheme() {
    const next: Theme = theme === "dark" ? "light" : "dark";
    setTheme(next);
    setCurrent(next);
  }

  async function handleSignOut() {
    setSigningOut(true);
    await signOut();
    navigate("/login", { replace: true });
  }

  return (
    <div className="flex min-h-screen">
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:fixed focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:bg-brand focus:px-4 focus:py-2 focus:font-medium focus:text-white"
      >
        Skip to main content
      </a>
      <aside className="w-56 shrink-0 border-r border-line bg-surface p-3">
        <p className="px-3 py-2 text-base font-semibold">VeilGate</p>
        <nav aria-label="Main">
          <ul className="mt-2 space-y-1">
            {navFor(session.claims.role).map(({ label, path, icon: Icon }) => (
              <li key={path}>
                <NavLink
                  to={path}
                  className={({ isActive }) =>
                    `flex items-center gap-3 rounded-lg px-3 py-2 ${
                      isActive
                        ? "bg-brand text-white"
                        : "text-muted hover:bg-line hover:text-fg"
                    }`
                  }
                >
                  <Icon className="size-4" aria-hidden="true" />
                  {label}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-end gap-3 border-b border-line bg-surface px-6 py-3">
          <span className="text-muted">{readable(session.claims.role)}</span>
          <Button
            variant="ghost"
            onClick={toggleTheme}
            aria-label={
              theme === "dark"
                ? "Switch to light theme"
                : "Switch to dark theme"
            }
          >
            {theme === "dark" ? (
              <Sun className="size-4" />
            ) : (
              <Moon className="size-4" />
            )}
          </Button>
          <Button
            variant="secondary"
            loading={signingOut}
            onClick={handleSignOut}
          >
            <LogOut className="size-4" aria-hidden="true" />
            Sign out
          </Button>
        </header>
        <div id="main-content" tabIndex={-1} className="flex-1 outline-none">
          <Outlet />
        </div>
      </div>

      {warning && <IdleWarning onStay={stayActive} />}
    </div>
  );
}
