import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation } from "@tanstack/react-query";
import { z } from "zod";
import { changePassword } from "../lib/accountApi";
import { ApiError } from "../lib/api";
import { describeApiError } from "../lib/apiErrorText";
import { passwordStrength } from "../lib/passwordStrength";
import { showToast } from "../lib/toast";
import Button from "./Button";
import PasswordField from "./PasswordField";

// The server checks these again (and a little more, such as the email name). Here they only save a round trip.
const schema = z
  .object({
    currentPassword: z.string().min(1, "Enter your current password"),
    newPassword: z.string().min(12, "Use at least 12 characters").max(128, "Use at most 128 characters"),
    confirm: z.string(),
  })
  .refine((values) => values.newPassword === values.confirm, {
    message: "The passwords do not match",
    path: ["confirm"],
  })
  .refine((values) => values.newPassword !== values.currentPassword, {
    message: "Choose a password you are not using now",
    path: ["newPassword"],
  });
type FormValues = z.infer<typeof schema>;

const BAR = ["bg-line", "bg-danger", "bg-warning", "bg-success", "bg-success"] as const;

/** The Password tab. A wrong current password counts toward the account lockout, so it is worth saying so before it happens. */
export default function PasswordPanel() {
  const [serverError, setServerError] = useState<string | null>(null);

  const {
    register,
    handleSubmit,
    watch,
    reset,
    setError,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: "onBlur",
    defaultValues: { currentPassword: "", newPassword: "", confirm: "" },
  });

  const change = useMutation({
    mutationFn: (values: FormValues) => changePassword(values.currentPassword, values.newPassword),
    onSuccess: () => {
      reset();
      showToast("Your password was changed. Your other devices were signed out.", "success");
    },
    onError: (error) => {
      if (error instanceof ApiError && error.code === "password.current_incorrect")
        setError("currentPassword", { message: error.message });
      else if (error instanceof ApiError && (error.code === "password.weak" || error.code === "password.same_as_current"))
        setError("newPassword", { message: error.message });
      else setServerError(describeApiError(error));
    },
  });

  const strength = passwordStrength(watch("newPassword"));

  return (
    <section aria-labelledby="password-heading" className="max-w-md space-y-4">
      <div>
        <h2 id="password-heading" className="text-lg font-semibold">
          Change password
        </h2>
        <p className="mt-1 text-muted">
          Longer is better: 12 to 128 characters, and a phrase beats a clever word. Changing it signs out all your other
          devices.
        </p>
      </div>

      <form
        noValidate
        onSubmit={handleSubmit((values) => {
          setServerError(null);
          change.mutate(values);
        })}
        className="space-y-4"
      >
        <PasswordField
          label="Current password"
          autoComplete="current-password"
          error={errors.currentPassword?.message}
          {...register("currentPassword")}
        />
        <div>
          <PasswordField
            label="New password"
            autoComplete="new-password"
            error={errors.newPassword?.message}
            {...register("newPassword")}
          />
          {strength.level > 0 && (
            <div className="mt-2 flex items-center gap-2" role="status">
              <div className="flex flex-1 gap-1" aria-hidden="true">
                {[1, 2, 3, 4].map((n) => (
                  <span key={n} className={`h-1.5 flex-1 rounded-full ${strength.level >= n ? BAR[strength.level] : "bg-line"}`} />
                ))}
              </div>
              <span className="text-sm text-muted">Strength: {strength.label}</span>
            </div>
          )}
        </div>
        <PasswordField
          label="Confirm new password"
          autoComplete="new-password"
          error={errors.confirm?.message}
          {...register("confirm")}
        />

        {serverError && (
          <p role="alert" className="text-danger">
            {serverError}
          </p>
        )}
        <Button type="submit" loading={change.isPending}>
          Change password
        </Button>
      </form>
    </section>
  );
}
