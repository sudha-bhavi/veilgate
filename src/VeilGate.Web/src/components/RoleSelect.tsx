import { useId } from "react";
import { ASSIGNABLE_ROLES } from "../lib/usersApi";

type Props = {
  value: string;
  onChange: (role: string) => void;
  label?: string;
};

/** The four roles a Tenant Admin can hand out, with a line saying what the chosen one allows. */
export default function RoleSelect({ value, onChange, label = "Role" }: Props) {
  const id = useId();
  const help = ASSIGNABLE_ROLES.find((r) => r.value === value)?.help;

  return (
    <div>
      <label htmlFor={id} className="mb-1 block text-muted">
        {label}
      </label>
      <select
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg focus-visible:outline-2 focus-visible:outline-accent"
      >
        {ASSIGNABLE_ROLES.map((role) => (
          <option key={role.value} value={role.value}>
            {role.label}
          </option>
        ))}
      </select>
      {help && <p className="mt-1 text-xs text-muted">{help}</p>}
    </div>
  );
}
