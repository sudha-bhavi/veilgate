import { Navigate, Outlet, useLocation } from "react-router-dom";
import { decideAccess } from "./access";
import { useSession } from "./useSession";

/** Wraps routes that need a signed-in person, optionally of certain roles. The server enforces the same rules on its own. */
export default function RequireAuth({ roles }: { roles?: readonly string[] }) {
  const session = useSession();
  const location = useLocation();

  switch (decideAccess(session, roles)) {
    case "wait":
      return <div className="p-6 text-muted">Loading…</div>;
    case "login":
      return (
        <Navigate
          to={`/login?returnTo=${encodeURIComponent(location.pathname + location.search)}`}
          replace
        />
      );
    case "forbidden":
      return <Navigate to="/403" replace />;
    case "allow":
      return <Outlet />;
  }
}
