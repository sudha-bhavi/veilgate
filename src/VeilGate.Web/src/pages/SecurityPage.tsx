import { useState } from "react";
import SessionsPanel from "../components/SessionsPanel";
import Tabs from "../components/Tabs";

// Later changes add the other tabs from the UI design (MFA and passkeys, Password, Security events) to this list.
const TABS = [{ id: "sessions", label: "Sessions" }] as const;
type TabId = (typeof TABS)[number]["id"];

/** "My account" security, for every signed-in role: it only ever shows and changes the signed-in person's own things. */
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
      </div>
    </main>
  );
}
