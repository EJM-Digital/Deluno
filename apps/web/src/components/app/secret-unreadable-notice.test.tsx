import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SecretUnreadableNotice, secretUnreadableText } from "./secret-unreadable-notice";

describe("the unreadable saved key notice", () => {
  it("says nothing when the saved key can be read", () => {
    expect(secretUnreadableText({ secretUnreadable: false })).toBeNull();
    expect(secretUnreadableText(null)).toBeNull();
    const { container } = render(<SecretUnreadableNotice item={{ secretUnreadable: false }} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("uses the reason the server gave, in plain words", () => {
    const problem = "The saved API key was stored on another computer and can't be read here. Enter it again.";
    render(<SecretUnreadableNotice item={{ secretUnreadable: true, secretProblem: problem }} />);
    expect(screen.getByRole("alert")).toHaveTextContent(problem);
  });

  it("falls back to the same plain sentence when no reason came with it", () => {
    expect(secretUnreadableText({ secretUnreadable: true })).toMatch(/stored on another computer and can't be read here\. Enter it again\./);
  });
});
