import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { AttentionDot, StatusPill, type Severity } from "./attention-dot";

afterEach(() => cleanup());

describe("the attention dot and the status pill (low rows)", () => {
  it("the dot is hidden from assistive tech, pulses only when asked, and keeps the colour of its severity", () => {
    const { container, rerender } = render(<AttentionDot severity="danger" />);
    const dot = () => container.firstElementChild as HTMLElement;
    expect(dot()).toHaveAttribute("aria-hidden", "true");
    expect(dot().className).toContain("bg-state-danger");
    expect(dot().className).not.toContain("animate-pulse");

    rerender(<AttentionDot severity="danger" pulse />);
    expect(dot().className).toContain("animate-pulse");
    expect(dot().className).toContain("bg-state-danger");
  });

  it.each([
    ["danger", "text-state-danger", "bg-state-danger"],
    ["warn", "text-state-warn", "bg-state-warn"],
    ["ok", "text-state-ok", "bg-state-ok"],
    ["info", "text-state-info", "bg-state-info"],
    ["neutral", "text-muted-foreground", "bg-muted-foreground/50"]
  ] as [Severity, string, string][])("a %s status pill shows its words with a matching dot", (severity, text, dotColour) => {
    const { container } = render(<StatusPill severity={severity}>Needs a look</StatusPill>);

    const pill = screen.getByText("Needs a look");
    expect(pill.className).toContain(text);
    expect(container.querySelector("[aria-hidden='true']")!.className).toContain(dotColour);
  });
});
