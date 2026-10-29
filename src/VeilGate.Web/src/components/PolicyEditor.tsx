import { useEffect, useRef, useState } from "react";
import Editor, { loader, type OnMount } from "@monaco-editor/react";
import * as monaco from "monaco-editor/editor/editor.api";
import "monaco-editor/editor/editor.main";
import editorWorker from "monaco-editor/editor/editor.worker?worker";
import type { PolicyIssue } from "../lib/policiesApi";

// Use the copy of Monaco bundled with the app instead of downloading it from a public CDN.
self.MonacoEnvironment = { getWorker: () => new editorWorker() };
loader.config({ monaco });

function useIsDark(): boolean {
  const [dark, setDark] = useState(() =>
    document.documentElement.classList.contains("dark"),
  );
  useEffect(() => {
    const observer = new MutationObserver(() =>
      setDark(document.documentElement.classList.contains("dark")),
    );
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["class"],
    });
    return () => observer.disconnect();
  }, []);
  return dark;
}

type Handle = {
  editor: Parameters<OnMount>[0];
  monaco: Parameters<OnMount>[1];
};

type Props = {
  value: string;
  onChange: (value: string) => void;
  readOnly: boolean;
  issues?: readonly PolicyIssue[];
};

const NO_ISSUES: readonly PolicyIssue[] = [];

export default function PolicyEditor({
  value,
  onChange,
  readOnly,
  issues = NO_ISSUES,
}: Props) {
  const dark = useIsDark();
  const handle = useRef<Handle | null>(null);
  const [ready, setReady] = useState(false);

  // Mark each problem on its line. An empty list clears the marks.
  useEffect(() => {
    const current = handle.current;
    const model = current?.editor.getModel();
    if (!current || !model) return;
    const lines = model.getLineCount();
    current.monaco.editor.setModelMarkers(
      model,
      "veilgate",
      issues.map((issue) => {
        const line = Math.min(Math.max(issue.line, 1), lines);
        const last = model.getLineMaxColumn(line);
        return {
          severity: current.monaco.MarkerSeverity.Error,
          message: issue.message,
          startLineNumber: line,
          startColumn: Math.min(Math.max(issue.column, 1), last),
          endLineNumber: line,
          endColumn: last,
        };
      }),
    );
  }, [issues, ready]);

  return (
    <div className="overflow-hidden rounded-xl border border-line">
      <Editor
        height="480px"
        language="yaml"
        theme={dark ? "vs-dark" : "vs"}
        value={value}
        onChange={(next) => onChange(next ?? "")}
        onMount={(editor, monacoInstance) => {
          handle.current = { editor, monaco: monacoInstance };
          setReady(true);
        }}
        options={{
          readOnly,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          fontSize: 13,
          tabSize: 2,
          insertSpaces: true,
          wordWrap: "on",
          ariaLabel: "Policy YAML",
        }}
      />
    </div>
  );
}
