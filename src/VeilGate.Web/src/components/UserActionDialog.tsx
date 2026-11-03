import { useState, type ReactNode } from "react";
import { useStepUpMutation } from "../auth/useStepUpMutation";
import { describeApiError } from "../lib/apiErrorText";
import type { UserAction } from "../lib/userRules";
import {
  changeUserRole,
  disableUser,
  enableUser,
  resendInvite,
  resetMfa,
  roleLabel,
  type TenantUser,
} from "../lib/usersApi";
import ConfirmDialog from "./ConfirmDialog";
import RoleSelect from "./RoleSelect";

export type ActionOutcome = { message: string; kind: "success" | "error" };

type Props = {
  user: TenantUser;
  action: UserAction;
  onDone: (outcome: ActionOutcome) => void;
  onCancel: () => void;
};

async function run(
  action: UserAction,
  user: TenantUser,
  role: string,
): Promise<ActionOutcome> {
  switch (action) {
    case "role":
      await changeUserRole(user.id, role);
      return { message: `${user.name} is now a ${roleLabel(role)}.`, kind: "success" };
    case "disable":
      await disableUser(user.id);
      return { message: `${user.name} was disabled.`, kind: "success" };
    case "enable":
      await enableUser(user.id);
      return { message: `${user.name} was enabled.`, kind: "success" };
    case "reset":
      await resetMfa(user.id);
      return {
        message: `Two-step sign-in was reset for ${user.name}. They set it up again at their next sign-in.`,
        kind: "success",
      };
    case "resend": {
      const result = await resendInvite(user.id);
      return result.emailSent
        ? { message: `A new invitation was sent to ${user.email}.`, kind: "success" }
        : {
            message: "The invitation was renewed, but the email could not be sent. Try again in a moment.",
            kind: "error",
          };
    }
  }
}

const COPY: Record<UserAction, { title: (name: string) => string; confirm: string; danger: boolean }> = {
  role: { title: (n) => `Change the role of ${n}`, confirm: "Change role", danger: false },
  disable: { title: (n) => `Disable ${n}?`, confirm: "Disable account", danger: true },
  enable: { title: (n) => `Enable ${n}?`, confirm: "Enable account", danger: false },
  reset: { title: (n) => `Reset two-step sign-in for ${n}?`, confirm: "Reset two-step sign-in", danger: true },
  resend: { title: (n) => `Send a new invitation to ${n}?`, confirm: "Send new invitation", danger: false },
};

function Explanation({ action, user }: { action: UserAction; user: TenantUser }): ReactNode {
  switch (action) {
    case "role":
      return "The new role applies right away. You may be asked to confirm your identity first.";
    case "disable":
      return `${user.name} is signed out everywhere and cannot sign in until you enable the account again.`;
    case "enable":
      return `${user.name} can sign in again.`;
    case "reset":
      return `This removes ${user.name}'s authenticator app and recovery codes and signs them out everywhere. At their next sign-in they set up two-step sign-in again. You may be asked to confirm your identity first.`;
    case "resend":
      return `A new link is emailed to ${user.email}. The old link stops working.`;
  }
}

/** One confirmation for each thing you can do to a person. Role changes and resets may ask for a fresh identity check first. */
export default function UserActionDialog({ user, action, onDone, onCancel }: Props) {
  const [role, setRole] = useState(user.role);
  const [failure, setFailure] = useState<string | null>(null);

  const { mutation, stepUpDialog } = useStepUpMutation({
    mutationFn: () => run(action, user, role),
    onSuccess: onDone,
    onFailure: (error) => setFailure(describeApiError(error)),
  });

  const copy = COPY[action];

  return (
    <>
      <ConfirmDialog
        title={copy.title(user.name)}
        confirmLabel={copy.confirm}
        danger={copy.danger}
        loading={mutation.isPending}
        confirmDisabled={action === "role" && role === user.role}
        error={failure}
        onConfirm={() => {
          setFailure(null);
          mutation.mutate();
        }}
        onCancel={onCancel}
      >
        {action === "role" && <RoleSelect value={role} onChange={setRole} />}
        <p className="text-muted">
          <Explanation action={action} user={user} />
        </p>
      </ConfirmDialog>
      {stepUpDialog}
    </>
  );
}
