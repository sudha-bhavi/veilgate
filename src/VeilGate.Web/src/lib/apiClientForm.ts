import type {
  ApiClient,
  CreateApiClientBody,
  UpdateApiClientBody,
} from "./apiClientsApi";
import { parseList } from "./textList";

export const MAX_RATE_LIMIT = 10_000;
export const MAX_ALLOWLIST = 50;

export type ApiClientFormState = {
  name: string;
  scopes: string[];
  rateLimit: string;
  ipAllowlist: string;
  defaultPolicyId: string;
};

export type ApiClientFormErrors = {
  name?: string;
  scopes?: string;
  rateLimit?: string;
  ipAllowlist?: string;
};

export const emptyClientForm = (): ApiClientFormState => ({
  name: "",
  scopes: ["gateway:invoke"],
  rateLimit: "",
  ipAllowlist: "",
  defaultPolicyId: "",
});

export const formFromClient = (client: ApiClient): ApiClientFormState => ({
  name: client.name,
  scopes: [...client.scopes],
  rateLimit: String(client.rateLimitPerMinute),
  ipAllowlist: client.ipAllowlist.join("\n"),
  defaultPolicyId: client.defaultPolicyId ?? "",
});

/** A blank box means "use the default" (null). Anything else must be a whole number in range. */
export function rateLimitOf(text: string): number | null | "invalid" {
  const trimmed = text.trim();
  if (trimmed === "") return null;
  const value = Number(trimmed);
  return Number.isInteger(value) && value >= 1 && value <= MAX_RATE_LIMIT
    ? value
    : "invalid";
}

export function clientFormErrors(
  form: ApiClientFormState,
  mode: "create" | "edit",
): ApiClientFormErrors {
  const errors: ApiClientFormErrors = {};
  if (mode === "create") {
    const name = form.name.trim();
    if (!name) errors.name = "Name is required.";
    else if (name.length > 80) errors.name = "Maximum 80 characters.";
  }
  if (form.scopes.length === 0) errors.scopes = "Pick at least one permission.";
  if (rateLimitOf(form.rateLimit) === "invalid") {
    errors.rateLimit = `A whole number from 1 to ${MAX_RATE_LIMIT.toLocaleString()}.`;
  }
  if (parseList(form.ipAllowlist).length > MAX_ALLOWLIST) {
    errors.ipAllowlist = `At most ${MAX_ALLOWLIST} networks.`;
  }
  return errors;
}

export function toCreateBody(form: ApiClientFormState): CreateApiClientBody {
  const rate = rateLimitOf(form.rateLimit);
  const networks = parseList(form.ipAllowlist);
  return {
    name: form.name.trim(),
    scopes: form.scopes,
    defaultPolicyId: form.defaultPolicyId || undefined,
    ipAllowlist: networks.length > 0 ? networks : undefined,
    rateLimitPerMinute: typeof rate === "number" ? rate : undefined,
  };
}

function sameSet(a: readonly string[], b: readonly string[]): boolean {
  const x = [...a].sort();
  const y = [...b].sort();
  return x.length === y.length && x.every((value, index) => value === y[index]);
}

/**
 * Only what changed, because a PATCH leaves out fields it does not mention. The default policy can be switched to another
 * one but not cleared: the server treats "no value" as "leave it".
 */
export function toUpdateBody(
  form: ApiClientFormState,
  original: ApiClient,
): UpdateApiClientBody {
  const body: UpdateApiClientBody = {};
  if (!sameSet(form.scopes, original.scopes)) body.scopes = form.scopes;

  const networks = parseList(form.ipAllowlist);
  if (networks.join("\n") !== original.ipAllowlist.join("\n")) {
    body.ipAllowlist = networks;
  }

  const rate = rateLimitOf(form.rateLimit);
  if (typeof rate === "number" && rate !== original.rateLimitPerMinute) {
    body.rateLimitPerMinute = rate;
  }

  if (
    form.defaultPolicyId &&
    form.defaultPolicyId !== (original.defaultPolicyId ?? "")
  ) {
    body.defaultPolicyId = form.defaultPolicyId;
  }
  return body;
}
