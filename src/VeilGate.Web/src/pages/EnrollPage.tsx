import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { QRCodeSVG } from "qrcode.react";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import AuthCard from "../components/AuthCard";
import Button from "../components/Button";
import CodeInput from "../components/CodeInput";
import CopyButton from "../components/CopyButton";
import { confirmEnrollment, startEnrollment } from "../auth/mfaApi";
import { mfaErrorOutcome } from "../lib/authErrors";
import { getMfaFlow, startMfaFlow } from "../lib/mfaFlow";

type Step = "scan" | "verify" | "codes";

function downloadCodes(codes: string[]) {
  const text = `VeilGate recovery codes\nEach code works once.\n\n${codes.join("\n")}\n`;
  const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = "veilgate-recovery-codes.txt";
  link.click();
  URL.revokeObjectURL(url);
}

export default function EnrollPage() {
  const location = useLocation();
  const navigate = useNavigate();
  // Read once when the page opens: finishing switches the stored flow to "verify", which must not trigger this page's own redirects.
  const [flow] = useState(getMfaFlow);
  const [step, setStep] = useState<Step>("scan");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [codes, setCodes] = useState<string[]>([]); // memory only: gone when the page is left
  const [saved, setSaved] = useState(false);

  // One request even if React runs this twice in development: asking again would replace the secret just shown.
  const setup = useQuery({
    queryKey: ["mfa-setup", flow?.token],
    queryFn: () => startEnrollment(flow!.token),
    enabled: flow?.purpose === "enroll",
    staleTime: Infinity,
    gcTime: 0,
    retry: false,
  });

  // Closing the page before the codes are saved would lose them for good.
  useEffect(() => {
    if (step !== "codes") return;
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [step]);

  if (!flow) return <Navigate to="/login" replace />;
  if (flow.purpose === "verify")
    return <Navigate to={`/login/mfa${location.search}`} replace />;
  if (setup.isError && mfaErrorOutcome(setup.error).kind === "expired")
    return <Navigate to="/login?reason=expired" replace />;

  const confirm = async (code: string) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await confirmEnrollment(code, flow.token);
      setCodes(result.recoveryCodes);
      setStep("codes");
    } catch (e) {
      const outcome = mfaErrorOutcome(e);
      if (outcome.kind === "expired" || outcome.kind === "locked") {
        navigate(
          `/login?reason=${outcome.kind === "locked" ? "locked" : "expired"}`,
          { replace: true },
        );
        return;
      }
      setError(outcome.message);
      setAttempt((a) => a + 1);
    } finally {
      setBusy(false);
    }
  };

  // Confirming does not sign anyone in, so the last step is the ordinary code screen with a fresh code from the new app.
  const finish = () => {
    const next = new URLSearchParams(location.search);
    next.set("reason", "enrolled");
    startMfaFlow(flow.token, "verify");
    setCodes([]);
    navigate(`/login/mfa?${next}`);
  };

  return (
    <AuthCard title="Set up two-step verification">
      <p className="mb-4 text-muted">
        Step {step === "scan" ? 1 : step === "verify" ? 2 : 3} of 3
      </p>

      {step === "scan" && (
        <div className="space-y-4">
          <p>
            Scan this QR code with an authenticator app (Microsoft
            Authenticator, Authy, Google Authenticator).
          </p>
          {setup.isPending && (
            <div className="mx-auto size-[200px] animate-pulse rounded-lg bg-line" />
          )}
          {setup.isError && (
            <div>
              <p role="alert" className="text-danger">
                Could not start the setup.
              </p>
              <Button className="mt-2" onClick={() => setup.refetch()}>
                Retry
              </Button>
            </div>
          )}
          {setup.isSuccess && (
            <>
              <div className="mx-auto w-fit rounded-lg bg-white p-3">
                <QRCodeSVG value={setup.data.otpAuthUri} size={200} />
              </div>
              <div>
                <p className="text-muted">Can&apos;t scan? Enter this setup key:</p>
                <p className="mt-1 break-all font-mono">
                  {setup.data.manualEntryKey}
                </p>
                <div className="mt-2">
                  <CopyButton text={setup.data.manualEntryKey} />
                </div>
                <p className="mt-1 text-xs text-muted">
                  The clipboard is not cleared automatically.
                </p>
              </div>
              <Button className="w-full" onClick={() => setStep("verify")}>
                Next
              </Button>
            </>
          )}
        </div>
      )}

      {step === "verify" && (
        <div className="space-y-3">
          <p>Enter the 6-digit code now shown in your app.</p>
          <div key={attempt} className={attempt > 0 ? "shake" : undefined}>
            <CodeInput
              label="Verification code"
              disabled={busy}
              onComplete={confirm}
            />
          </div>
          {error && (
            <p role="alert" className="text-danger">
              {error}
            </p>
          )}
        </div>
      )}

      {step === "codes" && (
        <div className="space-y-4">
          <p>
            Save these one-time recovery codes. They are shown only once, and
            each works a single time.
          </p>
          <ol className="grid grid-cols-2 gap-x-4 gap-y-1 font-mono">
            {codes.map((code) => (
              <li key={code}>{code}</li>
            ))}
          </ol>
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => downloadCodes(codes)}>
              Download .txt
            </Button>
            <CopyButton text={codes.join("\n")} label="Copy all" />
            <Button variant="secondary" onClick={() => window.print()}>
              Print
            </Button>
          </div>
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              checked={saved}
              onChange={(event) => setSaved(event.target.checked)}
            />
            I have saved these codes
          </label>
          <Button className="w-full" disabled={!saved} onClick={finish}>
            Finish and open VeilGate
          </Button>
        </div>
      )}
    </AuthCard>
  );
}
