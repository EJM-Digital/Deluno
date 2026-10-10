import { expect as baseExpect, test, type Page } from "@playwright/test";
import { authenticateAndNavigate } from "../helpers/auth-helper";

const expect = baseExpect.configure({ timeout: 20_000 });

// #1247: on the golden VM the indexer drawer's "Remove..." did not react to four clicks and the indexer was removed
// through the API instead. This walks the click a person makes for every kind of indexer: open its drawer, press
// Remove..., confirm in the dialog, and the indexer is gone from the list and from the server.

test.skip(({ isMobile }) => isMobile, "The indexer drawer is the same on a phone.");

type Kind = "torznab" | "newznab" | "tracker";

async function headers(page: Page) {
  const token = await page.evaluate(() => localStorage.getItem("deluno-auth-token"));
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function listIndexers(page: Page) {
  const response = await page.request.get("/api/indexers", { headers: await headers(page) });
  return await response.json() as { id: string; name: string }[];
}

/** Adds an indexer through the API and returns the name the server gave it (it names an address after its host, and a tracker after its definition). */
async function create(page: Page, kind: Kind, name: string) {
  const before = new Set((await listIndexers(page)).map((item) => item.id));
  const common = { name, privacy: "public", priority: 10, categories: "2000,5000", tags: "", mediaScope: "both", isEnabled: false };
  let data: Record<string, unknown>;
  if (kind === "tracker") {
    const catalog = await (await page.request.get("/api/trackers", { headers: await headers(page) })).json() as { id: string; links: string[] }[];
    expect(catalog.length, "the tracker catalog is empty").toBeGreaterThan(0);
    data = { ...common, protocol: "tracker", baseUrl: catalog[0].links[0] ?? "https://tracker.example.test", definitionId: catalog[0].id, definitionSettings: {}, definitionSecrets: {} };
  } else {
    data = { ...common, protocol: kind, baseUrl: `https://${name.toLowerCase()}.example.test`, apiKey: "draft-key" };
  }
  const response = await page.request.post("/api/indexers", { headers: await headers(page), data });
  expect(response.ok(), `POST /api/indexers (${kind}) answered ${response.status()}: ${await response.text()}`).toBe(true);
  const made = (await listIndexers(page)).find((item) => !before.has(item.id));
  expect(made, "the new indexer is not in the list").toBeTruthy();
  return made!;
}

for (const kind of ["torznab", "newznab", "tracker"] as const) {
  test(`pressing Remove in a ${kind} indexer's drawer asks, and removes it once confirmed (#1247)`, async ({ page }) => {
    test.setTimeout(90_000);
    await authenticateAndNavigate(page, "/indexers/indexers");
    const made = await create(page, kind, `Removal-${kind}-${Date.now()}`);
    const name = made.name;
    try {
      await page.goto("/indexers/indexers");
      await page.getByRole("row").filter({ hasText: name }).first().click();
      const drawer = page.getByRole("dialog", { name });
      await expect(drawer).toBeVisible();

      const remove = drawer.getByRole("button", { name: "Remove…" });
      await remove.scrollIntoViewIfNeeded();
      await expect(remove).toBeEnabled();
      await remove.click();

      const confirm = page.getByRole("alertdialog").or(page.getByRole("dialog", { name: new RegExp(`Remove .*${name}`) }));
      await expect(confirm.getByRole("button", { name: "Remove indexer" })).toBeVisible();
      await confirm.getByRole("button", { name: "Remove indexer" }).click();

      await expect(page.getByRole("row").filter({ hasText: name })).toHaveCount(0);
      expect((await listIndexers(page)).some((item) => item.id === made.id)).toBe(false);
    } finally {
      for (const item of (await listIndexers(page)).filter((candidate) => candidate.id === made.id)) {
        await page.request.delete(`/api/indexers/${item.id}`, { headers: await headers(page) }).catch(() => undefined);
      }
    }
  });
}

// What makes a click on Remove... do nothing (the likely cause on the golden VM): the button is disabled for as long as
// the drawer is busy with anything else, and a Test of an indexer that does not answer keeps it busy until the test
// gives up. The click is then swallowed by a disabled button without any message. A driver must wait for the button to
// be enabled (Playwright does; a coordinate click or a UI Automation Invoke does not), as a person would see it dimmed.
test("Remove stays dimmed while the drawer's own Test is still running, and works the moment it ends (#1247)", async ({ page }) => {
  test.setTimeout(90_000);
  await authenticateAndNavigate(page, "/indexers/indexers");
  const made = await create(page, "newznab", `Removal-busy-${Date.now()}`);
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  await page.route(/\/api\/indexers\/[^/]+\/test$/, async (route) => {
    await gate;
    await route.fulfill({ json: { healthStatus: "unreachable", message: "The indexer did not answer.", latencyMs: null } });
  });
  try {
    await page.goto("/indexers/indexers");
    await page.getByRole("row").filter({ hasText: made.name }).first().click();
    const drawer = page.getByRole("dialog", { name: made.name });
    await expect(drawer).toBeVisible();
    await drawer.getByRole("button", { name: "Test", exact: true }).click();

    const remove = drawer.getByRole("button", { name: "Remove…" });
    await expect(remove).toBeDisabled();
    await remove.click({ force: true });
    await expect(page.getByRole("dialog", { name: /^Remove / })).toHaveCount(0);

    release();
    await expect(remove).toBeEnabled();
    await remove.click();
    await page.getByRole("dialog", { name: /^Remove / }).getByRole("button", { name: "Remove indexer" }).click();
    await expect(page.getByRole("row").filter({ hasText: made.name })).toHaveCount(0);
  } finally {
    release();
    for (const item of (await listIndexers(page)).filter((candidate) => candidate.id === made.id)) {
      await page.request.delete(`/api/indexers/${item.id}`, { headers: await headers(page) }).catch(() => undefined);
    }
  }
});
