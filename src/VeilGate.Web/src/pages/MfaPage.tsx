import { useState } from "react";
import {
  Link,
  Navigate,
  useLocation,
  useNavigate,
  useSearchParams,
} from "react-router-dom";
import AuthCard from "../components/AuthCard";
import Button from "../components/Button";
import CodeInput from "../components/CodeInput";
import TextField from "../components/TextField";
import { verifyCode } from "../auth/mfaApi";
import { mfaErrorOutcome } from "../lib/authErrors";
import { formatCountdown } from "../lib/format";
import { clearMfaFlow, getMfaFlow, secondsLeft } from "../lib/mfaFlow";
import { safeReturnTo } from "../lib/returnTo";
import { setAccessToken } from "../lib/session";
import { useNow } from "../lib/useNow";

type Tab = "code" | "recovery";

function TabButton({
  selected,
  onClick,
  children,
}: {
  selected: boolean;
  onClick: () => void;
  children: string;
}) {
  return (
    <button
      role="tab"
      type="button"
      aria-selected={selected}
      onClick={onClick}
      className={`flex-1 rounded-md px-2 py-1.5 ${selected ? "bg-brand text-white" : "text-muted hover:text-fg"}`}
    >
      {children}
    </button>
  );
}

export default function MfaPage() {
  const [params] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const now = useNow(1000);
  const [tab, setTab] = useState<Tab>("code");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0); // changes after a failure: empties the boxes and replays the shake
  const [recovery, setRecovery] = useState("");

  // Read once when the page opens. Clearing the stored flow after a successful code must not make this page redirect itself
  // before the explicit navigation below has happened.
  const [flow] = useState(getMfaFlow);
  if (!flow) return <Navigate to="/login" replace />;
  if (flow.purpose === "enroll")
    return <Navigate to={`/mfa/enroll${location.search}`} replace />;

  const left = secondsLeft(flow, now);

  const submit = async (code: string) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await verifyCode(code, flow.token);
      setAccessToken(result.accessToken);
      clearMfaFlow();
      navigate(safeReturnTo(params.get("returnTo")), { replace: true });
    } catch (e) {
      const outcome = mfaErrorOutcome(e);
      if (outcome.kind === "expired" || outcome.kind === "locked") {
        clearMfaFlow();
        navigate(
          `/login?reason=${outcome.kind === "locked" ? "locked" : "expired"}`,
          { replace: true },
        );
        return;
      }
      setError(outcome.message);
      setAttempt((a) => a + 1);
      setBusy(false);
    }
  };

  const startOver = () => {
    clearMfaFlow();
    navigate("/login");
  };

  return (
    <AuthCard title="Two-step verification">
      {params.get("reason") === "enrolled" && (
        <p
          role="status"
          className="mb-4 rounded-lg border border-success/50 bg-success/10 p-3 text-success"
        >
          Two-step verification is set up. Enter the next code from your app to
          finish signing in.
        </p>
      )}

      {left === 0 ? (
        <div>
          <p role="alert" className="text-danger">
            Session expired
          </p>
          <Button className="mt-3" onClick={startOver}>
            Start over
          </Button>
        </div>
      ) : (
        <>
          <div
            role="tablist"
            className="mb-4 flex gap-1 rounded-lg border border-line p-1"
          >
            <TabButton
              selected={tab === "code"}
              onClick={() => setTab("code")}
            >
              Authenticator app
            </TabButton>
            <button
              role="tab"
              type="button"
              disabled
              aria-selected={false}
              title="Passkeys are not available yet"
              className="flex-1 cursor-not-allowed rounded-md px-2 py-1.5 text-muted opacity-50"
            >
              Passkey
            </button>
            <TabButton
              selected={tab === "recovery"}
              onClick={() => setTab("recovery")}
            >
              Recovery
            </TabButton>
          </div>

          {tab === "code" ? (
            <>
              <p className="mb-3 text-muted">
                Enter the 6-digit code from your app
              </p>
              <div key={attempt} className={attempt > 0 ? "shake" : undefined}>
                <CodeInput
                  label="Verification code"
                  disabled={busy}
                  onComplete={submit}
                />
              </div>
            </>
          ) : (
            <form
              onSubmit={(event) => {
                event.preventDefault();
                void submit(recovery.trim());
              }}
              className="space-y-3"
            >
              <TextField
                label="Recovery code"
                value={recovery}
                onChange={(event) => setRecovery(event.target.value)}
                placeholder="XXXXX-XXXXX"
                autoComplete="off"
                spellCheck={false}
                maxLength={12}
                autoFocus
              />
              <Button
                type="submit"
                className="w-full"
                loading={busy}
                disabled={!recovery.trim()}
              >
                Verify
              </Button>
            </form>
          )}

          {error && (
            <p role="alert" className="mt-3 text-danger">
              {error}
            </p>
          )}
          <p className="mt-4 text-muted">
            Code expires in {formatCountdown(left)}
          </p>
        </>
      )}

      <p className="mt-4">
        <Link
          to="/login"
          onClick={() => clearMfaFlow()}
          className="text-accent hover:underline"
        >
          Back to sign in
        </Link>
      </p>
    </AuthCard>
  );
}
