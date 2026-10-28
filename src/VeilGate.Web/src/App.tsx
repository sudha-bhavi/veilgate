import { useEffect } from "react";
import { Route, Routes } from "react-router-dom";
import { restoreSession } from "./lib/api";
import { ALL_TENANT_ROLES } from "./auth/roles";
import RequireAuth from "./auth/RequireAuth";
import Placeholder from "./pages/Placeholder";
import StatusPage from "./pages/StatusPage";
import LoginPage from "./pages/LoginPage";
import MfaPage from "./pages/MfaPage";
import EnrollPage from "./pages/EnrollPage";
import AppShell from "./components/AppShell";
import ToastHost from "./components/ToastHost";

export default function App() {
  // The access token lives in memory, so a reload loses it; the refresh cookie brings the session back.
  useEffect(() => {
    void restoreSession();
  }, []);

  return (
    <>
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
        </Route>
        <Route
          path="/403"
          element={
            <Placeholder title="You are not allowed to open this page" />
          }
        />
        <Route path="*" element={<Placeholder title="Page not found" />} />
      </Routes>
      <ToastHost />
    </>
  );
}
