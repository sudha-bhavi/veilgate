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
  entitiesProtected: Record<string, number>; // how many of each type, e.g. { PERSON: 1, PHONE: 2 }
  maskedText: string; // what the model was given
  llmResponseMasked: string; // what the model answered
  restoredResponse: string; // what the caller gets back
};

/** "3 items protected (PERSON 1, PHONE 2)" from the per-type counts a chat run returns. */
export function describeProtected(counts: Record<string, number>): string {
  const entries = Object.entries(counts);
  const total = entries.reduce((sum, [, count]) => sum + count, 0);
  if (total === 0) return "Nothing needed protecting";
  const items = total === 1 ? "1 item" : `${total} items`;
  return `${items} protected (${entries.map(([type, count]) => `${type} ${count}`).join(", ")})`;
}

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
