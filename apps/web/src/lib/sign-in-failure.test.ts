import { describe, expect, it } from "vitest";
import { RequestFailedError } from "./search-reasons";
import { describeAuthRefusal } from "./sign-in-failure";

const refusal = (status: number, body?: unknown, message?: string) =>
  new RequestFailedError(
    new Response(body === undefined ? "" : typeof body === "string" ? body : JSON.stringify(body), { status }),
    message
  );

describe("what the sign-in and first-run screens say when the server refuses (#1339)", () => {
  it("says the username or password is not right for the sign-in's own 401", async () => {
    const sentence = await describeAuthRefusal(refusal(401, undefined, "That username or password is not right."), "sign in");
    expect(sentence).toBe("That username or password is not right.");
  });

  it("uses the server's own sentence for a missing or wrong field", async () => {
    expect(await describeAuthRefusal(refusal(400, { message: "Choose a longer password." }), "create the account")).toBe("Choose a longer password.");
  });

  it("uses the first field error when the server sent no message, and for an account that already exists", async () => {
    expect(await describeAuthRefusal(refusal(400, { errors: { Password: ["The password is too short."] } }), "create the account")).toBe("The password is too short.");
    expect(await describeAuthRefusal(refusal(409, { message: "This install already has an account." }), "create the account")).toBe("This install already has an account.");
  });

  it("falls back to the shared sentence when the refusal has no usable wording or cannot be read", async () => {
    const empty = await describeAuthRefusal(refusal(400, { errors: {} }), "create the account");
    expect(await describeAuthRefusal(refusal(400, {}), "create the account")).toBe(empty);
    const unreadable = await describeAuthRefusal(refusal(400, "<html>not json</html>"), "create the account");
    expect(empty).not.toBe("");
    expect(unreadable).toBe(empty);
  });

  it("says a page's or provider's own sentence for an ordinary error, but not the browser's failed fetch", async () => {
    expect(await describeAuthRefusal(new Error("The sign-in provider is not set up."), "sign in")).toBe("The sign-in provider is not set up.");
    const offline = await describeAuthRefusal(new TypeError("Failed to fetch"), "sign in");
    expect(offline).toMatch(/^Deluno could not reach/);
    expect(offline).not.toBe("");
  });
});
