import { describe, expect, it } from "vitest";
import {
  datasetFormErrors,
  emptyDatasetForm,
  emptyEvalForm,
  evalFormErrors,
  toDatasetBody,
  toEvaluationBody,
} from "./evalForm";

describe("evalFormErrors", () => {
  it("asks for a dataset and a policy", () => {
    expect(evalFormErrors(emptyEvalForm())).toEqual({
      datasetId: "Choose a dataset.",
      policyId: "Choose a policy.",
    });
  });

  it("checks the recall target and the version", () => {
    const form = { ...emptyEvalForm(), datasetId: "d", policyId: "p" };
    expect(evalFormErrors({ ...form, threshold: "1.5" }).threshold).toBeDefined();
    expect(evalFormErrors({ ...form, threshold: "abc" }).threshold).toBeDefined();
    expect(evalFormErrors({ ...form, version: "0" }).version).toBeDefined();
    expect(evalFormErrors({ ...form, version: "2.5" }).version).toBeDefined();
    expect(evalFormErrors({ ...form, threshold: "", version: "" })).toEqual({});
  });
});

describe("toEvaluationBody", () => {
  it("sends numbers as numbers and leaves out blanks", () => {
    const form = { ...emptyEvalForm(), datasetId: "d", policyId: "p", version: "3" };
    expect(toEvaluationBody(form)).toEqual({
      datasetId: "d",
      policyId: "p",
      mode: "rules",
      recallThreshold: 0.98,
      policyVersion: 3,
    });
    expect(toEvaluationBody({ ...form, threshold: "", version: "" })).toMatchObject({
      recallThreshold: undefined,
      policyVersion: undefined,
    });
  });
});

describe("datasetFormErrors and toDatasetBody", () => {
  it("asks for a name, and limits the size", () => {
    expect(datasetFormErrors(emptyDatasetForm())).toEqual({ name: "Name is required." });
    const form = { ...emptyDatasetForm(), name: "Notes" };
    expect(datasetFormErrors({ ...form, count: "5001" }).count).toBeDefined();
    expect(datasetFormErrors({ ...form, seed: "1.5" }).seed).toBeDefined();
    expect(datasetFormErrors({ ...form, count: "200", seed: "42" })).toEqual({});
  });

  it("leaves out a blank count and seed", () => {
    expect(toDatasetBody({ name: " Notes ", count: "", seed: "" })).toEqual({
      name: "Notes",
      count: undefined,
      seed: undefined,
    });
    expect(toDatasetBody({ name: "Notes", count: "50", seed: "7" })).toEqual({
      name: "Notes",
      count: 50,
      seed: 7,
    });
  });
});
