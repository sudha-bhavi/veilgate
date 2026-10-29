import type { LlmProvider, ProviderBody } from "./providersApi";
import { parseList } from "./textList";

// The same limits the server enforces (LlmProvider.cs), so mistakes show before a round trip.
export const MODEL_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,79}$/;
export const MAX_MODELS = 50;
export const MAX_TIMEOUT_SECONDS = 300;
export const KEY_MIN = 8;
export const KEY_MAX = 512;

export type ProviderFormState = {
  name: string;
  baseUrl: string;
  apiKey: string;
  models: string;
  timeoutSeconds: string;
  enabled: boolean;
};

export type ProviderFormErrors = {
  name?: string;
  baseUrl?: string;
  apiKey?: string;
  models?: string;
  timeoutSeconds?: string;
};

export const emptyProviderForm = (): ProviderFormState => ({
  name: "",
  baseUrl: "", // a prefilled "https://" turned a pasted address into https://https//...
  apiKey: "",
  models: "",
  timeoutSeconds: "",
  enabled: true,
});

export const formFromProvider = (provider: LlmProvider): ProviderFormState => ({
  name: provider.name,
  baseUrl: provider.baseUrl,
  apiKey: "", // the stored key is never sent back; a blank box means "keep it"
  models: provider.models.join("\n"),
  timeoutSeconds: String(provider.timeoutSeconds),
  enabled: provider.enabled,
});

function addressProblem(value: string): string | null {
  const text = value.trim();
  if (!text) return "Address is required.";
  if (text.length > 300) return "Maximum 300 characters.";
  // new URL() accepts "https://https://host" as a host called "https", so catch the doubled scheme first.
  if (/^https?:\/\/https?:/i.test(text)) {
    return "The address starts with http twice. Enter it once.";
  }
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    return "Enter a full address such as https://api.example.com.";
  }
  if (url.protocol !== "https:" && url.protocol !== "http:") {
    return "Use an http or https address.";
  }
  if (url.username || url.password || url.search || url.hash) {
    return "Leave out credentials, a query and a fragment.";
  }
  // The gateway adds /v1/chat/completions itself (ProviderCaller.cs), so ".../v1" would call /v1/v1/... and get a 404.
  if (/\/v1\/*$/i.test(url.pathname)) {
    return "Leave out /v1 at the end: VeilGate adds /v1/chat/completions itself.";
  }
  return null;
}

export function providerFormErrors(
  form: ProviderFormState,
  mode: "create" | "edit",
): ProviderFormErrors {
  const errors: ProviderFormErrors = {};

  const name = form.name.trim();
  if (!name) errors.name = "Name is required.";
  else if (name.length > 80) errors.name = "Maximum 80 characters.";

  const address = addressProblem(form.baseUrl);
  if (address) errors.baseUrl = address;

  if (mode === "create" && !form.apiKey) {
    errors.apiKey = "API key is required.";
  } else if (form.apiKey && (form.apiKey.length < KEY_MIN || form.apiKey.length > KEY_MAX)) {
    errors.apiKey = `Between ${KEY_MIN} and ${KEY_MAX} characters.`;
  }

  const models = parseList(form.models);
  if (models.length === 0) errors.models = "At least one model is required.";
  else if (models.length > MAX_MODELS) errors.models = `At most ${MAX_MODELS} models.`;
  else if (models.some((model) => !MODEL_PATTERN.test(model))) {
    errors.models = "A model name has characters that are not allowed.";
  }

  const timeout = form.timeoutSeconds.trim();
  if (timeout !== "") {
    const seconds = Number(timeout);
    if (!Number.isInteger(seconds) || seconds < 1 || seconds > MAX_TIMEOUT_SECONDS) {
      errors.timeoutSeconds = `A whole number from 1 to ${MAX_TIMEOUT_SECONDS}.`;
    }
  }

  return errors;
}

export function toProviderBody(form: ProviderFormState): ProviderBody {
  const timeout = form.timeoutSeconds.trim();
  return {
    name: form.name.trim(),
    baseUrl: form.baseUrl.trim(),
    apiKey: form.apiKey || undefined,
    models: parseList(form.models),
    timeoutSeconds: timeout === "" ? undefined : Number(timeout),
    enabled: form.enabled,
  };
}

// The fixed words LlmProviderTester.cs reports. The provider's own error text never reaches the console.
const TEST_FAILURES: Record<string, string> = {
  timeout: "the provider took too long to answer",
  unreachable: "the address could not be reached",
  circuit_open: "paused after repeated failures, try again in a minute",
  overloaded: "too many calls are already waiting for it",
  http_error: "the provider refused the call (check the address, key and model)",
  invalid_response: "the answer was not an OpenAI-compatible reply",
};

/** A failed connection test in words, keeping the category for anyone who needs to look it up. */
export const describeTestFailure = (category: string | null): string =>
  category
    ? `Failed: ${TEST_FAILURES[category] ?? "unknown error"} (${category})`
    : "Failed: unknown error";

/** The body that re-saves a provider as it is, with one thing changed. No key, so the stored key stays. */
export function bodyFromProvider(
  provider: LlmProvider,
  changes: Partial<ProviderBody> = {},
): ProviderBody {
  return {
    name: provider.name,
    baseUrl: provider.baseUrl,
    models: provider.models,
    timeoutSeconds: provider.timeoutSeconds,
    enabled: provider.enabled,
    ...changes,
  };
}
