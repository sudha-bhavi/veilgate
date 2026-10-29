import { describe, expect, it } from "vitest";
import { classifyDiffLine } from "./diffLines";

describe("classifyDiffLine", () => {
  it("tells the kinds of line apart", () => {
    expect(classifyDiffLine("+PERSON: TOKENIZE")).toBe("add");
    expect(classifyDiffLine("-PERSON: REDACT")).toBe("remove");
    expect(classifyDiffLine("@@ -1,3 +1,3 @@")).toBe("hunk");
    expect(classifyDiffLine("--- version 1")).toBe("meta");
    expect(classifyDiffLine("+++ version 2")).toBe("meta");
    expect(classifyDiffLine(" version: 1")).toBe("context");
  });
});
