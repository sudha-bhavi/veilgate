import type { CreateDatasetBody, CreateEvaluationBody } from "./evaluationsApi";

export type EvalFormState = {
  datasetId: string;
  policyId: string;
  mode: "rules" | "rules+ner";
  threshold: string;
  version: string;
};

export type EvalFormErrors = {
  datasetId?: string;
  policyId?: string;
  threshold?: string;
  version?: string;
};

export const emptyEvalForm = (): EvalFormState => ({
  datasetId: "",
  policyId: "",
  mode: "rules",
  threshold: "0.98",
  version: "",
});

export function evalFormErrors(form: EvalFormState): EvalFormErrors {
  const errors: EvalFormErrors = {};
  if (!form.datasetId) errors.datasetId = "Choose a dataset.";
  if (!form.policyId) errors.policyId = "Choose a policy.";

  const threshold = form.threshold.trim();
  if (threshold !== "") {
    const value = Number(threshold);
    if (Number.isNaN(value) || value < 0 || value > 1) {
      errors.threshold = "A number from 0 to 1, for example 0.98.";
    }
  }

  const version = form.version.trim();
  if (version !== "") {
    const value = Number(version);
    if (!Number.isInteger(value) || value < 1) {
      errors.version = "A whole number from 1.";
    }
  }
  return errors;
}

export function toEvaluationBody(form: EvalFormState): CreateEvaluationBody {
  const threshold = form.threshold.trim();
  const version = form.version.trim();
  return {
    datasetId: form.datasetId,
    policyId: form.policyId,
    mode: form.mode,
    recallThreshold: threshold === "" ? undefined : Number(threshold),
    policyVersion: version === "" ? undefined : Number(version),
  };
}

export type DatasetFormState = { name: string; count: string; seed: string };
export type DatasetFormErrors = { name?: string; count?: string; seed?: string };

export const MAX_DATASET_NOTES = 5_000;

export const emptyDatasetForm = (): DatasetFormState => ({
  name: "",
  count: "",
  seed: "",
});

export function datasetFormErrors(form: DatasetFormState): DatasetFormErrors {
  const errors: DatasetFormErrors = {};
  const name = form.name.trim();
  if (!name) errors.name = "Name is required.";
  else if (name.length > 100) errors.name = "Maximum 100 characters.";

  const count = form.count.trim();
  if (count !== "") {
    const value = Number(count);
    if (!Number.isInteger(value) || value < 1 || value > MAX_DATASET_NOTES) {
      errors.count = `A whole number from 1 to ${MAX_DATASET_NOTES.toLocaleString()}.`;
    }
  }

  const seed = form.seed.trim();
  if (seed !== "" && !Number.isInteger(Number(seed))) {
    errors.seed = "A whole number.";
  }
  return errors;
}

export function toDatasetBody(form: DatasetFormState): CreateDatasetBody {
  const count = form.count.trim();
  const seed = form.seed.trim();
  return {
    name: form.name.trim(),
    count: count === "" ? undefined : Number(count),
    seed: seed === "" ? undefined : Number(seed),
  };
}
