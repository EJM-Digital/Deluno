import { expect, test, type Page } from "@playwright/test";
import { api, openScreen } from "../helpers/real";
import { untilQuiet } from "../helpers/settle";

/**
 * Find & Download > Remote path mappings: the list's columns, its heading, the clients a mapping applies to, and the server's
 * refusal of a mapping missing either path, against the real server (measure 4, issue 1296). The clients are disabled qBittorrent
 * addresses nobody listens on; nothing reaches another app.
 */

const stamp = `rpl${Date.now()}`;
const host = `maps-${stamp}.example.test`;
const ids: string[] = [];

interface Mapping { id: string; remotePath: string; localPath: string }
const client = async (page: Page, port: number) => {
  const made = await api<{ id: string }>(page, "POST", "/api/download-clients", { protocol: "qbittorrent", host, port, username: "u", password: "example-pw", moviesCategory: `movies${port}`, tvCategory: `tv${port}`, priority: 10, isEnabled: false });
  expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
  ids.push(made.body.id);
  return made.body.id;
};

test.describe("Remote path mapping list", () => {
  test.setTimeout(90_000);

  test.afterAll(async ({ browser }) => {
    const page = await browser.newPage();
    await openScreen(page, "/indexers/path-mappings");
    for (const id of ids) await api(page, "DELETE", `/api/download-clients/${id}`);
    await page.close();
  });

  test("a mapping is listed once with its host, client path, Deluno path and every client at that host, and the heading counts the mappings", async ({ page }) => {
    await openScreen(page, "/indexers/path-mappings");
    const first = await client(page, 8081);
    await client(page, 8082);
    const made = await api<Mapping>(page, "POST", `/api/download-clients/${first}/path-mappings`, { remotePath: `/remote/${stamp}`, localPath: "/local/media" });
    expect(made.status, JSON.stringify(made.body)).toBeLessThan(300);
    await page.reload();
    await untilQuiet(page);
    for (const heading of ["Host", "Client path", "Deluno path", "Applies to"]) await expect(page.getByRole("columnheader", { name: heading })).toBeVisible();
    const rows = page.getByRole("row").filter({ hasText: `/remote/${stamp}` });
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toContainText(host);
    await expect(rows.first()).toContainText("/local/media");
    const total = (await Promise.all(ids.map(async (id) => (await api<Mapping[]>(page, "GET", `/api/download-clients/${id}/path-mappings`)).body.length))).reduce((sum, count) => sum + count, 0);
    expect(total).toBeGreaterThanOrEqual(1);
    const countWords = await page.getByText(/^[0-9]+ mappings?$/).innerText();
    expect(Number(countWords.split(" ")[0])).toBe(await page.getByRole("row").filter({ has: page.getByRole("button", { name: "Edit", exact: true }) }).count());
    const applies = await rows.first().getByRole("cell").nth(3).innerText();
    expect(applies.split(",").length).toBe(2);
    expect(applies).not.toContain("No client at this host yet");
  });

  test("the server refuses a mapping missing either path, in words, and stores nothing", async ({ page }) => {
    await openScreen(page, "/indexers/path-mappings");
    const owner = await client(page, 8083);
    const before = (await api<Mapping[]>(page, "GET", `/api/download-clients/${owner}/path-mappings`)).body.length;
    for (const body of [{ remotePath: "", localPath: "/local/media" }, { remotePath: "/remote/x", localPath: " " }, { remotePath: "", localPath: "" }]) {
      const reply = await api<{ errors?: Record<string, string[]> }>(page, "POST", `/api/download-clients/${owner}/path-mappings`, body);
      expect(reply.status).toBe(400);
      expect(reply.body.errors?.pathMapping).toEqual(["Both the client path and Deluno path are required."]);
    }
    expect((await api<Mapping[]>(page, "GET", `/api/download-clients/${owner}/path-mappings`)).body.length).toBe(before);
  });
});
