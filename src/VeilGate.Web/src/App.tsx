import { useEffect } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { restoreSession } from "./lib/api";
import {
  ALL_TENANT_ROLES,
  API_CLIENT_ROLES,
  AUDIT_ROLES,
  EVALUATION_ROLES,
  PLAYGROUND_ROLES,
  PROVIDER_ROLES,
} from "./auth/roles";
import RequireAuth from "./auth/RequireAuth";
import { rolesForPath } from "./auth/nav";
import AppShell from "./components/AppShell";
import ErrorBoundary from "./components/ErrorBoundary";
import ToastHost from "./components/ToastHost";
import Placeholder from "./pages/Placeholder";
import StatusPage from "./pages/StatusPage";
import LoginPage from "./pages/LoginPage";
import MfaPage from "./pages/MfaPage";
import EnrollPage from "./pages/EnrollPage";
import ForbiddenPage from "./pages/ForbiddenPage";
import NotFoundPage from "./pages/NotFoundPage";
import PlaygroundPage from "./pages/PlaygroundPage";
import { POLICY_AUTHOR_ROLES } from "./auth/policyAccess";
import PoliciesPage from "./pages/PoliciesPage";
import NewPolicyPage from "./pages/NewPolicyPage";
import PolicyDetailPage from "./pages/PolicyDetailPage";
import ApiClientsPage from "./pages/ApiClientsPage";
import NewApiClientPage from "./pages/NewApiClientPage";
import ApiClientDetailPage from "./pages/ApiClientDetailPage";
import ProvidersPage from "./pages/ProvidersPage";
import EvaluationsPage from "./pages/EvaluationsPage";
import EvaluationDetailPage from "./pages/EvaluationDetailPage";
import EvaluationComparePage from "./pages/EvaluationComparePage";
import AuditLogPage from "./pages/AuditLogPage";
import ComingSoonPage from "./pages/ComingSoonPage";
import { COMING_SOON } from "./pages/comingSoon";

export default function App() {
  // The access token lives in memory, so a reload loses it; the refresh cookie brings the session back.
  useEffect(() => {
    void restoreSession();
  }, []);

  return (
    <ErrorBoundary>
      <Routes>
        {/* The home address opens the console: the dashboard, or the sign-in page first when signed out */}
        <Route path="/" element={<Navigate to="/dashboard" replace />} />
        {/* A quick "can the console reach the API?" check, open without signing in */}
        <Route path="/status" element={<StatusPage />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/login/mfa" element={<MfaPage />} />
        <Route path="/mfa/enroll" element={<EnrollPage />} />
        <Route
          path="/forgot-password"
          element={<Placeholder title="Forgot password" />}
        />

        {/* Every signed-in tenant role */}
        <Route element={<RequireAuth roles={ALL_TENANT_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/policies" element={<PoliciesPage />} />
            <Route path="/policies/:id" element={<PolicyDetailPage />} />
          </Route>
        </Route>

        <Route element={<RequireAuth roles={POLICY_AUTHOR_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/policies/new" element={<NewPolicyPage />} />
          </Route>
        </Route>

        <Route element={<RequireAuth roles={PLAYGROUND_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/playground" element={<PlaygroundPage />} />
          </Route>
        </Route>

        {/* The same roles the server allows for each area */}
        <Route element={<RequireAuth roles={API_CLIENT_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/clients" element={<ApiClientsPage />} />
            <Route path="/clients/new" element={<NewApiClientPage />} />
            <Route path="/clients/:id" element={<ApiClientDetailPage />} />
          </Route>
        </Route>

        <Route element={<RequireAuth roles={PROVIDER_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/providers" element={<ProvidersPage />} />
          </Route>
        </Route>

        <Route element={<RequireAuth roles={EVALUATION_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/evaluations" element={<EvaluationsPage />} />
            <Route
              path="/evaluations/compare"
              element={<EvaluationComparePage />}
            />
            <Route
              path="/evaluations/:id"
              element={<EvaluationDetailPage />}
            />
          </Route>
        </Route>

        <Route element={<RequireAuth roles={AUDIT_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/audit" element={<AuditLogPage />} />
          </Route>
        </Route>

        {/* Sections that are planned but not built yet. Who may open each comes from the sidebar list. */}
        {COMING_SOON.map(({ path, ...page }) => (
          <Route
            key={path}
            element={<RequireAuth roles={rolesForPath(path)} />}
          >
            <Route element={<AppShell />}>
              <Route path={path} element={<ComingSoonPage {...page} />} />
            </Route>
          </Route>
        ))}

        {/* Any signed-in person: the 403 page and anything that does not exist */}
        <Route element={<RequireAuth />}>
          <Route element={<AppShell />}>
            <Route path="/403" element={<ForbiddenPage />} />
            <Route path="*" element={<NotFoundPage />} />
          </Route>
        </Route>
      </Routes>
      <ToastHost />
    </ErrorBoundary>
  );
}
