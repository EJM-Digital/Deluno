import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SecretUnreadableNotice, repeatsSecretNotice, secretUnreadableText } from "./secret-unreadable-notice";

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

describe("a health message that repeats the notice (#1168 C7)", () => {
  const item = { secretUnreadable: true, secretProblem: "The saved password was stored on another computer and can't be read here. Enter it again." };

  it("is left out of the drawer, so the sentence is said once", () => {
    expect(repeatsSecretNotice(item, item.secretProblem)).toBe(true);
    expect(repeatsSecretNotice(item, "The saved password was stored on another computer, so this computer cannot read it.")).toBe(true);
  });

  it("is kept when it says something else, or when the saved key can be read", () => {
    expect(repeatsSecretNotice(item, "Connection refused.")).toBe(false);
    expect(repeatsSecretNotice({ secretUnreadable: false }, item.secretProblem)).toBe(false);
    expect(repeatsSecretNotice(item, null)).toBe(false);
  });
});
