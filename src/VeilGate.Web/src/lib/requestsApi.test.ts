import { describe, expect, it } from "vitest";
import { entitySummary, requestCaller, type GatewayRequest } from "./requestsApi";

const request = (over: Partial<GatewayRequest> = {}): GatewayRequest => ({
  id: "m1",
  requestId: "req_1",
  occurredAt: "2026-11-03T10:00:00Z",
  actorType: "client",
  clientId: "c1",
  clientName: "ehr-bridge",
  action: "gateway.chat",
  status: "success",
  reason: null,
  model: null,
  provider: null,
  policyVersion: null,
  entityCount: 0,
  entities: {},
  latencyMs: null,
  ...over,
});

describe("requestCaller", () => {
  it("uses the name while the client exists", () => {
    expect(requestCaller(request())).toBe("ehr-bridge");
  });

  it("names a caller that no longer exists instead of leaving the cell empty", () => {
    expect(requestCaller(request({ clientName: null }))).toBe("API client c1 (no longer exists)");
    expect(requestCaller(request({ clientName: null, actorType: "user" }))).toBe("Person c1 (no longer exists)");
  });
});

describe("entitySummary", () => {
  it("lists the counts by type in a stable order", () => {
    expect(entitySummary({ SSN: 1, PHONE: 2 })).toBe("PHONE 2, SSN 1");
  });

  it("says None when nothing was protected", () => {
    expect(entitySummary({})).toBe("None");
  });
});
