import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { AttentionBadge, AttentionDot, StatusPill, type Severity } from "./attention-dot";

const severities: Array<[Severity, string]> = [
  ["danger", "state-danger"], ["warn", "state-warn"], ["ok", "state-ok"], ["info", "state-info"], ["neutral", "hairline"]
];

describe("attention marks in every severity", () => {
  it.each(severities)("a %s badge, pill and dot use the %s colour", (severity, token) => {
    const { container } = render(
      <>
        <AttentionBadge count={3} severity={severity} />
        <StatusPill severity={severity}>Pill</StatusPill>
        <AttentionDot severity={severity} pulse className="extra" />
      </>
    );
    expect(screen.getByText("3").className).toContain(token === "hairline" ? "ring-hairline" : token);
    expect(screen.getByText("Pill").className).toContain(token === "hairline" ? "border-hairline" : token);
    expect(container.querySelector("span.animate-pulse")).not.toBeNull();
  });

  it("shows 99+ for a large count, a count of 0 as nothing, and a neutral badge by default", () => {
    const { container, rerender } = render(<AttentionBadge count={120} />);
    expect(screen.getByText("99+")).toBeTruthy();
    rerender(<AttentionBadge count={0} />);
    expect(container.textContent).toBe("");
  });
});
