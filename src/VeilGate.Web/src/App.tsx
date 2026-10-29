import { useEffect } from "react";
import { Route, Routes } from "react-router-dom";
import { restoreSession } from "./lib/api";
import { ALL_TENANT_ROLES, PLAYGROUND_ROLES } from "./auth/roles";
import RequireAuth from "./auth/RequireAuth";
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

export default function App() {
  // The access token lives in memory, so a reload loses it; the refresh cookie brings the session back.
  useEffect(() => {
    void restoreSession();
  }, []);

  return (
    <ErrorBoundary>
      <Routes>
        <Route path="/" element={<StatusPage />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/login/mfa" element={<MfaPage />} />
        <Route path="/mfa/enroll" element={<EnrollPage />} />
        <Route
          path="/forgot-password"
          element={<Placeholder title="Forgot password" />}
        />
        <Route element={<RequireAuth roles={ALL_TENANT_ROLES} />}>
          <Route element={<AppShell />}>
            <Route
              path="/dashboard"
              element={<Placeholder title="Dashboard" />}
            />
          </Route>
          <Route path="/policies" element={<PoliciesPage />} />
          <Route path="/policies/:id" element={<PolicyDetailPage />} />
        </Route>
        <Route element={<RequireAuth roles={POLICY_AUTHOR_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/policies/new" element={<NewPolicyPage />} />
          </Route>
        </Route>
        <Route element={<RequireAuth />}>
          <Route element={<AppShell />}>
            <Route path="/403" element={<ForbiddenPage />} />
            <Route path="*" element={<NotFoundPage />} />
          </Route>
        </Route>
        <Route element={<RequireAuth roles={PLAYGROUND_ROLES} />}>
          <Route element={<AppShell />}>
            <Route path="/playground" element={<PlaygroundPage />} />
          </Route>
        </Route>
      </Routes>
      <ToastHost />
    </ErrorBoundary>
  );
}
