import {
  useState,
  type InputHTMLAttributes,
  type KeyboardEvent,
  type Ref,
} from "react";
import TextField from "./TextField";

type Props = Omit<InputHTMLAttributes<HTMLInputElement>, "type"> & {
  label: string;
  error?: string;
  ref?: Ref<HTMLInputElement>;
};

/** A password box with a show/hide toggle and a Caps Lock warning. Spellcheck is off so the text never goes to a spell service. */
export default function PasswordField({ onKeyUp, onKeyDown, ...props }: Props) {
  const [visible, setVisible] = useState(false);
  const [capsLock, setCapsLock] = useState(false);

  const track = (event: KeyboardEvent<HTMLInputElement>) =>
    setCapsLock(event.getModifierState("CapsLock"));

  return (
    <div>
      <TextField
        {...props}
        type={visible ? "text" : "password"}
        spellCheck={false}
        onKeyDown={(event) => {
          track(event);
          onKeyDown?.(event);
        }}
        onKeyUp={(event) => {
          track(event);
          onKeyUp?.(event);
        }}
        trailing={
          <button
            type="button"
            onClick={() => setVisible((v) => !v)}
            aria-label={visible ? "Hide password" : "Show password"}
            aria-pressed={visible}
            className="rounded px-1 text-muted hover:text-fg"
          >
            {visible ? "Hide" : "Show"}
          </button>
        }
      />
      {capsLock && <p className="mt-1 text-warning">Caps Lock is on</p>}
    </div>
  );
}
