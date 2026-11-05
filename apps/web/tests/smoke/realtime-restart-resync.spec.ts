import { expect, test } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

// #1497: after Deluno restarted, an open Dashboard stayed frozen. The page reconnected, but the hub answered its resume
// with the enum's number (2) and the page waits for the words "ResyncRequired", so it never refetched and dropped every
// push of the new run. This reads the real hub's answer on the wire, as the page does, for a page that names another run.
test.describe("the hub's answer to a resume is in words the page reads (#1497)", () => {
  test("a page whose numbers came from another server run is told to resync, in words", async ({ page }) => {
    await authenticateAndNavigate(page, "/");
    // The smoke host does not take WebSockets through its preview proxy, so this speaks the hub's long-polling transport by hand.
    const answer = await page.evaluate(async () => {
      const token = localStorage.getItem("deluno-auth-token") ?? "";
      const headers = { Authorization: `Bearer ${token}` };
      const negotiated = await (await fetch("/hubs/deluno/negotiate?negotiateVersion=1", { method: "POST", headers })).json();
      const url = `/hubs/deluno?id=${encodeURIComponent(negotiated.connectionToken ?? negotiated.connectionId)}`;
      const end = String.fromCharCode(0x1e);
      const send = (frame: unknown) => fetch(url, { method: "POST", headers, body: `${typeof frame === "string" ? frame : JSON.stringify(frame)}${end}` });
      await send({ protocol: "json", version: 1 });
      let invoked = false;
      for (let poll = 0; poll < 20; poll++) {
        const reply = await fetch(url, { headers });
        if (reply.status !== 200) break;
        for (const part of (await reply.text()).split(end).filter(Boolean)) {
          const frame = JSON.parse(part) as { type?: number; result?: { status: unknown; instanceId: unknown } };
          if (frame.type === undefined && !invoked) {
            invoked = true;
            await send({ type: 1, invocationId: "1", target: "Resume", arguments: [5, ["dashboard"], "a-run-that-is-gone"] });
          } else if (frame.type === 3 && frame.result) {
            await fetch(url, { method: "DELETE", headers });
            return frame.result;
          }
        }
      }
      throw new Error("the hub never answered");
    });
    expect(answer.status).toBe("ResyncRequired");
    expect(typeof answer.instanceId).toBe("string");
  });
});
