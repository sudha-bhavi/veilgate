import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { describeApiError } from "../lib/apiErrorText";
import { inviteUser, type InviteResult } from "../lib/usersApi";
import ConfirmDialog from "./ConfirmDialog";
import RoleSelect from "./RoleSelect";
import TextField from "./TextField";

type Props = {
  onInvited: (result: InviteResult) => void;
  onCancel: () => void;
};

export default function InviteUserDialog({ onInvited, onCancel }: Props) {
  const [email, setEmail] = useState("");
  const [role, setRole] = useState("Developer");
  const invite = useMutation({
    mutationFn: () => inviteUser(email.trim(), role),
    onSuccess: onInvited,
  });

  return (
    <ConfirmDialog
      title="Invite someone"
      confirmLabel="Send invitation"
      loading={invite.isPending}
      confirmDisabled={!email.trim()}
      error={invite.isError ? describeApiError(invite.error) : null}
      onConfirm={() => invite.mutate()}
      onCancel={onCancel}
    >
      <TextField
        label="Email"
        type="email"
        autoComplete="off"
        autoFocus
        value={email}
        onChange={(event) => setEmail(event.target.value)}
      />
      <RoleSelect value={role} onChange={setRole} />
      <p className="text-xs text-muted">
        They get an email with a link that works once and lasts 48 hours. They
        choose their own name and password.
      </p>
    </ConfirmDialog>
  );
}
