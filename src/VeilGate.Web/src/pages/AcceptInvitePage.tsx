import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import AuthCard from "../components/AuthCard";
import Button from "../components/Button";
import PasswordField from "../components/PasswordField";
import TextField from "../components/TextField";
import { describeApiError } from "../lib/apiErrorText";
import { ApiError } from "../lib/api";
import { startMfaFlow } from "../lib/mfaFlow";
import { acceptInvitation, getInvitation, roleLabel } from "../lib/usersApi";

// The server checks these again (and a little more). Here they only save a round trip.
const schema = z
  .object({
    fullName: z.string().trim().min(1, "Enter your name").max(120, "Use at most 120 characters"),
    password: z.string().min(12, "Use at least 12 characters").max(128, "Use at most 128 characters"),
    confirm: z.string(),
  })
  .refine((values) => values.password === values.confirm, {
    message: "The passwords do not match",
    path: ["confirm"],
  });
type FormValues = z.infer<typeof schema>;

/** The page behind the emailed link. Nobody is signed in: the token in the address is the only proof. */
export default function AcceptInvitePage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const token = params.get("token") ?? "";
  const [serverError, setServerError] = useState<string | null>(null);

  const invitation = useQuery({
    queryKey: ["invitation", token],
    queryFn: () => getInvitation(token),
    enabled: token !== "",
    retry: false,
    staleTime: Infinity,
    gcTime: 0,
  });

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: "onBlur",
    defaultValues: { fullName: "", password: "", confirm: "" },
  });

  async function onSubmit(values: FormValues) {
    setServerError(null);
    try {
      const result = await acceptInvitation({
        token,
        fullName: values.fullName,
        password: values.password,
      });
      if (!result.mfaToken) {
        setServerError("Something went wrong. Try again.");
        return;
      }
      // The account exists now. Two-step setup is next, exactly as for a first sign-in.
      startMfaFlow(result.mfaToken, "enroll");
      navigate("/mfa/enroll", { replace: true });
    } catch (error) {
      setServerError(describeApiError(error));
    }
  }

  const linkIsDead =
    token === "" ||
    (invitation.isError && invitation.error instanceof ApiError && invitation.error.status === 404);

  if (linkIsDead) {
    return (
      <AuthCard title="This link is no longer valid">
        <p className="text-muted">
          An invitation link works once and lasts 48 hours. Ask the person who invited you to send a new one.
        </p>
        <Link to="/login" className="mt-4 inline-block text-accent underline">
          Go to sign in
        </Link>
      </AuthCard>
    );
  }

  if (invitation.isError) {
    return (
      <AuthCard title="Accept your invitation">
        <p role="alert" className="text-danger">
          {describeApiError(invitation.error)}
        </p>
        <Button className="mt-3" variant="secondary" onClick={() => invitation.refetch()}>
          Retry
        </Button>
      </AuthCard>
    );
  }

  if (invitation.isPending) {
    return (
      <AuthCard title="Accept your invitation">
        <div className="h-40 animate-pulse rounded-lg bg-line" aria-label="Loading your invitation" />
      </AuthCard>
    );
  }

  const invite = invitation.data;

  return (
    <AuthCard title="Accept your invitation">
      <p className="mb-4 text-muted">
        You were invited to join <strong className="text-fg">{invite.tenantName}</strong> as{" "}
        {roleLabel(invite.role)}.
      </p>

      <form onSubmit={handleSubmit(onSubmit)} noValidate className="space-y-4">
        <TextField label="Email" type="email" value={invite.email} readOnly />
        <TextField
          label="Full name"
          autoComplete="name"
          autoFocus
          error={errors.fullName?.message}
          {...register("fullName")}
        />
        <PasswordField
          label="Password"
          autoComplete="new-password"
          error={errors.password?.message}
          {...register("password")}
        />
        <PasswordField
          label="Confirm password"
          autoComplete="new-password"
          error={errors.confirm?.message}
          {...register("confirm")}
        />
        <p className="text-xs text-muted">At least 12 characters. A longer phrase is better than a complicated word.</p>

        {serverError && (
          <p role="alert" className="text-danger">
            {serverError}
          </p>
        )}

        <Button type="submit" className="w-full" loading={isSubmitting}>
          Create account
        </Button>
      </form>

      <p className="mt-4 text-xs text-muted">Next you set up two-step sign-in with an authenticator app.</p>
    </AuthCard>
  );
}
