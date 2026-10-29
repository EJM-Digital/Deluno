import { describe, expect, it } from "vitest";
import { readAlreadyDownloading } from "./already-downloading-prompt";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

describe("readAlreadyDownloading", () => {
  it("reads the question from the already-downloading 409", async () => {
    expect(await readAlreadyDownloading(json(409, { code: "already-downloading", message: "It is already downloading." }))).toEqual({ message: "It is already downloading." });
  });

  it("ignores a 409 with another code, a 409 with no message, a body that is not JSON and a 200", async () => {
    expect(await readAlreadyDownloading(json(409, { code: "something-else", message: "No." }))).toBeNull();
    expect(await readAlreadyDownloading(json(409, { code: "already-downloading" }))).toBeNull();
    expect(await readAlreadyDownloading(new Response("<html>conflict</html>", { status: 409 }))).toBeNull();
    expect(await readAlreadyDownloading(json(200, { code: "already-downloading", message: "It is already downloading." }))).toBeNull();
  });
});
