import { useState } from "react";
import SecurityEventsPanel from "../components/SecurityEventsPanel";
import SessionsPanel from "../components/SessionsPanel";
import Tabs from "../components/Tabs";

// A later change adds the other tabs from the UI design (MFA and passkeys, Password) to this list.
const TABS = [
  { id: "sessions", label: "Sessions" },
  { id: "events", label: "Security events" },
] as const;
type TabId = (typeof TABS)[number]["id"];

/** "My account" security, for every signed-in role: sessions are always your own, and events are your own unless your role oversees the organization. */
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
        {tab === "events" && <SecurityEventsPanel />}
      </div>
    </main>
  );
}
