import { describe, expect, it } from "vitest";
import { plainText } from "./plain-text";

describe("plainText", () => {
  it("keeps the words and drops tags and line breaks", () => {
    expect(plainText("Use your <b>passkey</b><br/>from the site &amp; paste it")).toBe("Use your passkey from the site & paste it");
  });

  it("does not let a tag split around another one come back together", () => {
    expect(plainText("<scr<b>ipt>alert(1)</scr</b>ipt>")).not.toMatch(/[<>]/);
    expect(plainText("<<b>script>x")).not.toContain("<script");
  });
});
