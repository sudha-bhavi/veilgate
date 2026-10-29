import type { KeyboardEvent } from "react";

type Props<T extends string> = {
  /** What the group of tabs is for, read out by screen readers. */
  label: string;
  tabs: readonly { id: T; label: string }[];
  active: T;
  onChange: (id: T) => void;
  /** The id of the panel that shows the active tab's content. */
  panelId: string;
};

/**
 * Tabs that work from the keyboard as the pattern asks: only the active tab is in the Tab order, and the arrow keys,
 * Home and End move between tabs. Each tab's button gets the id `tab-<id>`, for the panel's aria-labelledby.
 */
export default function Tabs<T extends string>({
  label,
  tabs,
  active,
  onChange,
  panelId,
}: Props<T>) {
  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const index = tabs.findIndex((tab) => tab.id === active);
    let next: number;
    if (event.key === "ArrowRight") next = (index + 1) % tabs.length;
    else if (event.key === "ArrowLeft") next = (index - 1 + tabs.length) % tabs.length;
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = tabs.length - 1;
    else return;

    event.preventDefault();
    onChange(tabs[next].id);
    document.getElementById(`tab-${tabs[next].id}`)?.focus();
  }

  return (
    <div
      role="tablist"
      aria-label={label}
      onKeyDown={onKeyDown}
      className="flex gap-1 border-b border-line"
    >
      {tabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          id={`tab-${tab.id}`}
          aria-selected={active === tab.id}
          aria-controls={panelId}
          tabIndex={active === tab.id ? 0 : -1}
          onClick={() => onChange(tab.id)}
          className={`-mb-px border-b-2 px-4 py-2 font-medium ${
            active === tab.id
              ? "border-accent text-fg"
              : "border-transparent text-muted hover:text-fg"
          }`}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}
