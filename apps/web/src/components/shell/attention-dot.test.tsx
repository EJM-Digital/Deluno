import { cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { AttentionBadge, AttentionDot, type Severity } from "./attention-dot";

// The coloured dot and count badge that mark what needs a look (shell area). The colour of each severity is its own.

afterEach(() => cleanup());

const DOT: Record<Severity, string> = {
  danger: "bg-state-danger",
  warn: "bg-state-warn",
  ok: "bg-state-ok",
  info: "bg-state-info",
  neutral: "bg-muted-foreground/50"
};

describe("AttentionDot and AttentionBadge", () => {
  it.each(Object.keys(DOT) as Severity[])("the dot for %s is drawn in its own colour", (severity) => {
    const { container } = render(<AttentionDot severity={severity} />);
    expect(container.firstElementChild!.className).toContain(DOT[severity]);
    for (const other of Object.values(DOT).filter((value) => value !== DOT[severity])) {
      expect(container.firstElementChild!.className).not.toContain(other);
    }
  });

  it("the badge is a number in its severity's colour and caps at 99+", () => {
    const { container, unmount } = render(<AttentionBadge count={3} severity="danger" />);
    expect(container.textContent).toBe("3");
    expect(container.firstElementChild!.className).toContain("text-state-danger");
    unmount();

    const { container: capped } = render(<AttentionBadge count={120} severity="warn" />);
    expect(capped.textContent).toBe("99+");
    expect(capped.firstElementChild!.className).toContain("text-state-warn");
  });

  it("a zero or negative count draws no badge at all", () => {
    const { container: zero } = render(<AttentionBadge count={0} severity="danger" />);
    expect(zero).toBeEmptyDOMElement();
    const { container: negative } = render(<AttentionBadge count={-1} />);
    expect(negative).toBeEmptyDOMElement();
  });
});
