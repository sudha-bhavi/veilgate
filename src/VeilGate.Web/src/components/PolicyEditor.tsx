import { useEffect, useState } from "react";
import Editor, { loader } from "@monaco-editor/react";
import * as monaco from "monaco-editor/editor/editor.api";
import "monaco-editor/editor/editor.main";
import editorWorker from "monaco-editor/editor/editor.worker?worker";

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

type Props = {
  value: string;
  onChange: (value: string) => void;
  readOnly: boolean;
};

export default function PolicyEditor({ value, onChange, readOnly }: Props) {
  const dark = useIsDark();
  return (
    <div className="overflow-hidden rounded-xl border border-line">
      <Editor
        height="480px"
        language="yaml"
        theme={dark ? "vs-dark" : "vs"}
        value={value}
        onChange={(next) => onChange(next ?? "")}
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
