import { apiFetch } from "./api";
import { listPolicyPage, type PolicyListItem } from "./policiesApi";

export type Dataset = {
  id: string;
  name: string;
  caseCount: number;
  createdAt: string;
};

export type TypeReport = {
  tp: number;
  fp: number;
  fn: number;
  precision: number;
  recall: number;
};

export type EvalResult = {
  recall: number;
  precision: number;
  f1: number;
  leakRate: number;
  leaks: number;
  gatePassed: boolean;
  perType: Record<string, TypeReport>;
};

export type EvalState = "Queued" | "Running" | "Succeeded" | "Failed";

export type EvalRun = {
  id: string;
  datasetId: string;
  policyId: string;
  policyVersion: number;
  mode: string;
  recallThreshold: number;
  state: EvalState;
  progress: { done: number; total: number };
  attempts: number;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  errorCode: string | null;
  result: EvalResult | null;
};

/** One sensitive value that got through. Datasets are synthetic, so showing the value is safe. */
export type LeakDetail = {
  note: number | null;
  type: string;
  start: number;
  end: number;
  reason: string;
  value: string | null;
};

/** Run B against run A. A positive delta means B is higher. */
export type MetricChange = { a: number; b: number; delta: number };

export type EvalComparison = {
  a: string;
  b: string;
  datasetId: string;
  recall: MetricChange;
  precision: MetricChange;
  f1: MetricChange;
  leakRate: MetricChange;
  leaksA: number;
  leaksB: number;
  gateA: boolean;
  gateB: boolean;
  perType: Record<string, { recall: MetricChange; precision: MetricChange }>;
};

export const listDatasets = async (): Promise<Dataset[]> =>
  (await apiFetch<{ items: Dataset[] }>("/api/v1/datasets")).items;

export type CreateDatasetBody = { name: string; count?: number; seed?: number };

export const createDataset = (body: CreateDatasetBody) =>
  apiFetch<Dataset>("/api/v1/datasets/synthetic", { method: "POST", body });

/** Policies a run can use: any that is not archived. A draft can be tried before it is submitted. */
export async function listEvaluablePolicies(): Promise<PolicyListItem[]> {
  const page = await listPolicyPage({ pageSize: 100 });
  return page.items.filter((policy) => policy.status !== "Archived");
}

export type CreateEvaluationBody = {
  datasetId: string;
  policyId: string;
  policyVersion?: number;
  mode?: string;
  recallThreshold?: number;
};

/** Only queues the run; a worker does the work, so the run is read back by polling. */
export const createEvaluation = (body: CreateEvaluationBody) =>
  apiFetch<EvalRun>("/api/v1/evaluations", { method: "POST", body });

export const listEvaluations = (page = 1, pageSize = 20) =>
  apiFetch<{ items: EvalRun[]; page: number; pageSize: number }>(
    `/api/v1/evaluations?page=${page}&pageSize=${pageSize}`,
  );

export const getEvaluation = (id: string) =>
  apiFetch<EvalRun>(`/api/v1/evaluations/${id}`);

export const getLeaks = (id: string, page = 1, pageSize = 50) =>
  apiFetch<{ items: LeakDetail[]; page: number; pageSize: number }>(
    `/api/v1/evaluations/${id}/leaks?page=${page}&pageSize=${pageSize}`,
  );

export const compareEvaluations = (a: string, b: string) =>
  apiFetch<EvalComparison>(
    `/api/v1/evaluations/compare?a=${encodeURIComponent(a)}&b=${encodeURIComponent(b)}`,
  );
