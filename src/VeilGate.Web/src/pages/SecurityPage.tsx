import { useState } from "react";
import MfaPanel from "../components/MfaPanel";
import PasswordPanel from "../components/PasswordPanel";
import SecurityEventsPanel from "../components/SecurityEventsPanel";
import SessionsPanel from "../components/SessionsPanel";
import Tabs from "../components/Tabs";

// The order of the UI design (7.1). Passkeys will join the MFA tab when they are built.
const TABS = [
  { id: "sessions", label: "Sessions" },
  { id: "mfa", label: "MFA" },
  { id: "password", label: "Password" },
  { id: "events", label: "Security events" },
] as const;
type TabId = (typeof TABS)[number]["id"];

/** "My account" security, for every signed-in role: sessions, two-step sign-in and password are always your own, and events are your own unless your role oversees the organization. */
export default function SecurityPage() {
  const [tab, setTab] = useState<TabId>("sessions");

  return (
    <main className="mx-auto max-w-6xl space-y-6 p-6">
      <header>
        <h1 className="text-xl font-semibold">Security</h1>
        <p className="mt-1 text-muted">Your sign-in sessions and account protection.</p>
      </header>

      <Tabs label="Security" tabs={TABS} active={tab} onChange={setTab} panelId="security-panel" />

      <div id="security-panel" role="tabpanel" aria-labelledby={`tab-${tab}`}>
        {tab === "sessions" && <SessionsPanel />}
        {tab === "mfa" && <MfaPanel />}
        {tab === "password" && <PasswordPanel />}
        {tab === "events" && <SecurityEventsPanel />}
      </div>
    </main>
  );
}
