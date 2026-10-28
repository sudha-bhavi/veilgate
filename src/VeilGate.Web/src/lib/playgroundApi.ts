import { apiFetch } from "./api";

export type Mode = "detect" | "deidentify" | "chat";

export type DetectResult = {
  requestId: string;
  entities: { type: string; start: number; end: number; confidence: number }[];
};

export type DeidentifyResult = {
  requestId: string;
  entities: { type: string; start: number; end: number; action: string }[];
  maskedText: string;
};

export type ChatResult = {
  requestId: string;
  entitiesProtected: number;
  maskedText: string; // what the model was given
  llmResponseMasked: string; // what the model answered
  restoredResponse: string; // what the caller gets back
};

export type PlaygroundOutcome =
  | { mode: "detect"; data: DetectResult }
  | { mode: "deidentify"; data: DeidentifyResult }
  | { mode: "chat"; data: ChatResult };

export type RunInput = {
  mode: Mode;
  text: string;
  model?: string;
  policyId?: string;
};

export async function runPlayground(
  input: RunInput,
): Promise<PlaygroundOutcome> {
  const data = await apiFetch<DetectResult | DeidentifyResult | ChatResult>(
    "/api/v1/playground/run",
    {
      method: "POST",
      body: {
        mode: input.mode,
        text: input.text,
        model: input.mode === "chat" ? input.model : undefined,
        policyId: input.policyId || undefined, // an empty choice means the tenant default
      },
    },
  );
  return { mode: input.mode, data } as PlaygroundOutcome;
}

export type PolicyOption = {
  id: string;
  name: string;
  status: string;
  currentVersion: number | null;
};

/** Policies a run can use: only those with a published version. */
export async function listPolicies(): Promise<PolicyOption[]> {
  const page = await apiFetch<{ items: PolicyOption[] }>(
    "/api/v1/policies?pageSize=50",
  );
  return page.items.filter(
    (policy) => policy.currentVersion !== null && policy.status !== "Archived",
  );
}
