import { useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { describeApiError } from "../lib/apiErrorText";
import { demoIsAvailable, startDemo } from "../lib/demoApi";
import { safeReturnTo } from "../lib/returnTo";
import { setAccessToken } from "../lib/session";
import Button from "./Button";

/** "Try the demo" on the sign-in page. It appears only when the server says the demo is switched on. */
export default function DemoButton() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [available, setAvailable] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void demoIsAvailable().then((yes) => {
      if (!cancelled) setAvailable(yes);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  if (!available) return null;

  async function start() {
    setBusy(true);
    setError(null);
    try {
      const result = await startDemo();
      setAccessToken(result.accessToken);
      navigate(safeReturnTo(params.get("returnTo")), { replace: true });
    } catch (e) {
      setError(describeApiError(e));
      setBusy(false);
    }
  }

  return (
    <div className="mt-6 border-t border-line pt-4">
      <p className="mb-3 text-muted">
        Just looking? Try VeilGate with sample data. The demo is read-only.
      </p>
      <Button variant="secondary" className="w-full" loading={busy} onClick={start}>
        Try the demo
      </Button>
      {error && (
        <p role="alert" className="mt-2 text-danger">
          {error}
        </p>
      )}
    </div>
  );
}
