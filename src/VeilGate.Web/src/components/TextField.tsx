import {
  useId,
  type InputHTMLAttributes,
  type ReactNode,
  type Ref,
} from "react";

type Props = InputHTMLAttributes<HTMLInputElement> & {
  label: string;
  error?: string;
  /** Something shown inside the field on the right, such as the show/hide button. */
  trailing?: ReactNode;
  ref?: Ref<HTMLInputElement>;
};

export default function TextField({
  label,
  error,
  trailing,
  id,
  className = "",
  ref,
  ...rest
}: Props) {
  const generated = useId();
  const inputId = id ?? generated;
  const errorId = `${inputId}-error`;

  return (
    <div>
      <label htmlFor={inputId} className="mb-1 block text-muted">
        {label}
      </label>
      <div className="relative">
        <input
          id={inputId}
          ref={ref}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          className={`w-full rounded-lg border bg-surface px-3 py-2 text-fg outline-none focus-visible:border-accent ${error ? "border-danger" : "border-line"} ${trailing ? "pr-16" : ""} ${className}`}
          {...rest}
        />
        {trailing && (
          <div className="absolute inset-y-0 right-2 flex items-center">
            {trailing}
          </div>
        )}
      </div>
      {error && (
        <p id={errorId} role="alert" className="mt-1 text-danger">
          {error}
        </p>
      )}
    </div>
  );
}
