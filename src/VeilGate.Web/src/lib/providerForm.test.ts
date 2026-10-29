import { describe, expect, it } from "vitest";
import type { LlmProvider } from "./providersApi";
import {
  bodyFromProvider,
  describeTestFailure,
  emptyProviderForm,
  formFromProvider,
  providerFormErrors,
  toProviderBody,
} from "./providerForm";

const provider = (over: Partial<LlmProvider> = {}): LlmProvider => ({
  id: "pr1",
  name: "Main",
  baseUrl: "https://api.example.com",
  keyLast4: "abcd",
  models: ["model-a", "model-b"],
  timeoutSeconds: 60,
  priority: 0,
  enabled: true,
  createdAt: "2026-01-01T00:00:00Z",
  ...over,
});

const valid = () => ({
  ...emptyProviderForm(),
  name: "Main",
  baseUrl: "https://api.example.com",
  apiKey: "key-12345678",
  models: "model-a",
});

describe("providerFormErrors", () => {
  it("asks for everything on an empty form", () => {
    expect(providerFormErrors(emptyProviderForm(), "create")).toEqual({
      name: "Name is required.",
      baseUrl: "Address is required.",
      apiKey: "API key is required.",
      models: "At least one model is required.",
    });
  });

  it("accepts a complete form", () => {
    expect(providerFormErrors(valid(), "create")).toEqual({});
  });

  it("refuses an address with credentials or a query", () => {
    for (const baseUrl of ["https://user:pw@example.com", "https://example.com/?key=1", "ftp://example.com"]) {
      expect(providerFormErrors({ ...valid(), baseUrl }, "create").baseUrl).toBeDefined();
    }
  });

  it("refuses an address with the scheme twice, which the URL parser would accept", () => {
    expect(providerFormErrors({ ...valid(), baseUrl: "https://http://localhost:8089" }, "create").baseUrl).toBe(
      "The address starts with http twice. Enter it once.",
    );
  });

  it("refuses /v1 at the end, because the gateway adds it", () => {
    for (const baseUrl of ["https://api.example.com/v1", "https://api.example.com/v1/", "http://localhost:8089/V1"]) {
      expect(providerFormErrors({ ...valid(), baseUrl }, "create").baseUrl).toBe(
        "Leave out /v1 at the end: VeilGate adds /v1/chat/completions itself.",
      );
    }
    expect(providerFormErrors({ ...valid(), baseUrl: "https://gateway.test/openai" }, "create")).toEqual({});
  });

  it("lets the key stay blank when editing, but checks it when given", () => {
    expect(providerFormErrors({ ...valid(), apiKey: "" }, "edit")).toEqual({});
    expect(providerFormErrors({ ...valid(), apiKey: "short" }, "edit").apiKey).toBeDefined();
  });

  it("checks model names and the timeout", () => {
    expect(providerFormErrors({ ...valid(), models: "ok\nbad name!" }, "create").models).toBeDefined();
    expect(providerFormErrors({ ...valid(), timeoutSeconds: "301" }, "create").timeoutSeconds).toBeDefined();
    expect(providerFormErrors({ ...valid(), timeoutSeconds: "30" }, "create")).toEqual({});
  });
});

describe("toProviderBody", () => {
  it("trims, parses the model list and leaves out a blank key and timeout", () => {
    const body = toProviderBody({
      ...valid(),
      name: " Main ",
      apiKey: "",
      models: "model-a, model-b\nmodel-a",
    });
    expect(body).toEqual({
      name: "Main",
      baseUrl: "https://api.example.com",
      apiKey: undefined,
      models: ["model-a", "model-b"],
      timeoutSeconds: undefined,
      enabled: true,
    });
  });
});

describe("describeTestFailure", () => {
  it("puts a known category into words and keeps the category", () => {
    expect(describeTestFailure("timeout")).toBe("Failed: the provider took too long to answer (timeout)");
  });

  it("does not invent a meaning for a category it does not know", () => {
    expect(describeTestFailure("something_new")).toBe("Failed: unknown error (something_new)");
    expect(describeTestFailure(null)).toBe("Failed: unknown error");
  });
});

describe("formFromProvider and bodyFromProvider", () => {
  it("never puts a key in the form", () => {
    const form = formFromProvider(provider());
    expect(form.apiKey).toBe("");
    expect(form.models).toBe("model-a\nmodel-b");
  });

  it("re-saves a provider with one change and no key", () => {
    expect(bodyFromProvider(provider(), { enabled: false })).toEqual({
      name: "Main",
      baseUrl: "https://api.example.com",
      models: ["model-a", "model-b"],
      timeoutSeconds: 60,
      enabled: false,
    });
  });
});
