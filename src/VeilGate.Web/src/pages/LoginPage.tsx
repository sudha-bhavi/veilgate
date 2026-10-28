import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import {
  Link,
  Navigate,
  useLocation,
  useNavigate,
  useSearchParams,
} from "react-router-dom";
import AuthCard from "../components/AuthCard";
import Button from "../components/Button";
import PasswordField from "../components/PasswordField";
import TextField from "../components/TextField";
import { login } from "../auth/authApi";
import { useSession } from "../auth/useSession";
import { loginErrorMessage } from "../lib/authErrors";
import { startMfaFlow } from "../lib/mfaFlow";
import { safeReturnTo } from "../lib/returnTo";

const schema = z.object({
  email: z
    .string()
    .trim()
    .max(254, "Enter a valid email")
    .email("Enter a valid email"),
  password: z.string().min(1),
});
type FormValues = z.infer<typeof schema>;

const BANNERS: Record<string, string> = {
  expired: "Your session expired. Sign in again.",
  idle: "You were signed out after a period of inactivity.",
};

export default function LoginPage() {
  const [params] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const session = useSession();
  const [serverError, setServerError] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    control,
    setValue,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: "onBlur",
    defaultValues: { email: "", password: "" },
  });
  const [email, password] = useWatch({ control, name: ["email", "password"] });

  // Someone who is already signed in has no business here.
  if (session.status === "signedIn")
    return <Navigate to={safeReturnTo(params.get("returnTo"))} replace />;

  async function onSubmit(values: FormValues) {
    setServerError(null);
    try {
      const result = await login(values.email, values.password);
      if (
        !result.mfaToken ||
        !(result.mfaRequired || result.mfaEnrollmentRequired)
      ) {
        setServerError("Something went wrong. Try again.");
        return;
      }
      startMfaFlow(result.mfaToken, result.mfaRequired ? "verify" : "enroll");
      // location.search carries returnTo along to the next step.
      navigate(
        `${result.mfaRequired ? "/login/mfa" : "/mfa/enroll"}${location.search}`,
      );
    } catch (error) {
      setServerError(loginErrorMessage(error));
      setValue("password", ""); // a wrong password is not kept around
    }
  }

  const reason = params.get("reason");
  const banner = reason ? BANNERS[reason] : undefined;

  return (
    <AuthCard title="Sign in to VeilGate">
      {banner && (
        <p
          role="status"
          className="mb-4 rounded-lg border border-warning/50 bg-warning/10 p-3 text-warning"
        >
          {banner}
        </p>
      )}

      <form onSubmit={handleSubmit(onSubmit)} noValidate className="space-y-4">
        <TextField
          label="Email"
          type="email"
          autoComplete="username"
          placeholder="name@company.com"
          autoFocus
          error={errors.email?.message}
          {...register("email")}
        />
        <PasswordField
          label="Password"
          autoComplete="current-password"
          {...register("password")}
        />

        <div className="text-right">
          <Link to="/forgot-password" className="text-accent hover:underline">
            Forgot password?
          </Link>
        </div>

        {serverError && (
          <p role="alert" className="text-danger">
            {serverError}
          </p>
        )}

        <Button
          type="submit"
          className="w-full"
          loading={isSubmitting}
          disabled={!email || !password}
        >
          Continue
        </Button>
      </form>

      <p className="mt-4 text-xs text-muted">
        Protected by MFA. No real patient data should be entered in the demo.
      </p>
    </AuthCard>
  );
}
