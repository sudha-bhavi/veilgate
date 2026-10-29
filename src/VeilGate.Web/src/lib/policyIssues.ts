import type { PolicyIssue } from "./policiesApi";

export const describeIssue = (issue: PolicyIssue): string =>
  issue.line > 0
    ? `Line ${issue.line}, column ${Math.max(issue.column, 1)}: ${issue.message}`
    : issue.message;
