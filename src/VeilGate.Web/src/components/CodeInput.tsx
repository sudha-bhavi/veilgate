import {
  useEffect,
  useRef,
  useState,
  type ClipboardEvent,
  type KeyboardEvent,
} from "react";
import {
  applyInput,
  CODE_LENGTH,
  removeAt,
  typedIntoBox,
} from "../lib/codeInput";

type Props = {
  label: string;
  disabled?: boolean;
  onComplete: (code: string) => void;
};

const AUTO_SUBMIT_MS = 150;

/**
 * Six boxes for a 6-digit code: auto-advance, paste, Backspace and arrow keys. It calls `onComplete` 150 ms after the sixth digit.
 * To empty it after a failure, the parent changes its React `key`, which remounts it empty and focused.
 */
export default function CodeInput({ label, disabled, onComplete }: Props) {
  const [boxes, setBoxes] = useState<string[]>(() =>
    Array<string>(CODE_LENGTH).fill(""),
  );
  const refs = useRef<(HTMLInputElement | null)[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(() => () => clearTimeout(timer.current), []);

  function update(result: { boxes: string[]; focus: number }) {
    setBoxes(result.boxes);
    refs.current[result.focus]?.focus();
    clearTimeout(timer.current);
    if (result.boxes.every(Boolean)) {
      timer.current = setTimeout(
        () => onComplete(result.boxes.join("")),
        AUTO_SUBMIT_MS,
      );
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>, index: number) {
    if (event.key === "Backspace") {
      event.preventDefault();
      update(removeAt(boxes, index));
    } else if (event.key === "ArrowLeft") {
      event.preventDefault();
      refs.current[Math.max(0, index - 1)]?.focus();
    } else if (event.key === "ArrowRight") {
      event.preventDefault();
      refs.current[Math.min(CODE_LENGTH - 1, index + 1)]?.focus();
    }
  }

  function onPaste(event: ClipboardEvent<HTMLInputElement>, index: number) {
    event.preventDefault();
    update(applyInput(boxes, index, event.clipboardData.getData("text")));
  }

  return (
    <div role="group" aria-label={label} className="flex justify-between gap-2">
      {boxes.map((digit, index) => (
        <input
          key={index}
          ref={(element) => {
            refs.current[index] = element;
          }}
          value={digit}
          disabled={disabled}
          inputMode="numeric"
          autoComplete={index === 0 ? "one-time-code" : "off"}
          autoFocus={index === 0}
          spellCheck={false}
          aria-label={`${label} digit ${index + 1}`}
          className="size-11 rounded-lg border border-line bg-surface text-center text-lg text-fg outline-none focus-visible:border-accent disabled:opacity-50"
          onChange={(event) =>
            update(applyInput(boxes, index, typedIntoBox(event.target.value)))
          }
          onKeyDown={(event) => onKeyDown(event, index)}
          onPaste={(event) => onPaste(event, index)}
          onFocus={(event) => event.target.select()}
        />
      ))}
    </div>
  );
}
