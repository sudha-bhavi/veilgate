import { describe, expect, it } from "vitest";
import { eventSubject, eventTitle, severityLook } from "./securityEventText";
import type { SecurityEvent } from "./securityEventsApi";

const event = (over: Partial<SecurityEvent> = {}): SecurityEvent => ({
  id: "e1",
  occurredAt: "2026-11-04T12:00:00Z",
  severity: "Medium",
  type: "mfa.reset",
  userId: null,
  userName: null,
  clientId: null,
  clientName: null,
  ip: null,
  details: {},
  ...over,
});

describe("severityLook", () => {
  it("groups the five severities into info, warning and critical", () => {
    expect(severityLook("Info").level).toBe("info");
    expect(severityLook("Low").level).toBe("info");
    expect(severityLook("Medium")).toEqual({ tone: "warning", level: "warning" });
    expect(severityLook("High")).toEqual({ tone: "danger", level: "critical" });
    expect(severityLook("Critical").level).toBe("critical");
  });
});

describe("eventTitle", () => {
  it("explains the types we know", () => {
    expect(eventTitle("auth.account_locked")).toMatch(/locked/i);
    expect(eventTitle("leak.gate_tripped")).toMatch(/leak/i);
  });

  it("shows an unknown type as it is", () => {
    expect(eventTitle("something.new")).toBe("something.new");
  });
});

describe("eventSubject", () => {
  it("names the person, or says they are gone", () => {
    expect(eventSubject(event({ userId: "u1", userName: "Ann Lee (ann@acme.test)" }))).toBe("Ann Lee (ann@acme.test)");
    expect(eventSubject(event({ userId: "u1" }))).toMatch(/no longer exists/);
  });

  it("names the API client, or says it is gone", () => {
    expect(eventSubject(event({ clientId: "c1", clientName: "ehr-bridge" }))).toBe("API client ehr-bridge");
    expect(eventSubject(event({ clientId: "c1" }))).toMatch(/no longer exists/);
  });

  it("has a dash when the event is about nobody in particular", () => {
    expect(eventSubject(event())).toBe("—");
  });
});
