import { expect, test } from "@playwright/test";
import { api, openScreen } from "../helpers/real";

/**
 * Retention's rule fields against the real server (measure 4, issue 1270): GET /api/retention/fields serves, for each level, the
 * retention fields that level can be asked plus the catalogue's fields, with the media servers each one is not available on; a season
 * and an episode are asked their show's catalogue questions.
 */

interface Field { id: string; label: string; group: string; valueKind: string; operators: string[]; source: string; unsupportedOn: string[] }
const fields = async (page: import("@playwright/test").Page, level: string) => (await api<Field[]>(page, "GET", `/api/retention/fields?level=${level}`)).body;

test("each level is served its retention fields and the catalogue's, a season and an episode the show's, with the servers a field cannot be read from", async ({ page }) => {
  await openScreen(page, "/search-cycles/retention");
  const byLevel: Record<string, Field[]> = {};
  for (const level of ["movie", "show", "season", "episode"]) {
    byLevel[level] = await fields(page, level);
    expect(byLevel[level].length, level).toBeGreaterThan(0);
    expect(new Set(byLevel[level].map((field) => field.id)).size, `${level} ids are unique`).toBe(byLevel[level].length);
    for (const field of byLevel[level]) {
      expect(field.label.trim(), field.id).not.toBe("");
      expect(field.operators.length, field.id).toBeGreaterThan(0);
      expect(field.source.length, field.id).toBeGreaterThan(0);
      expect(Array.isArray(field.unsupportedOn), field.id).toBe(true);
    }
  }
  // The retention fields come from the media servers, the catalogue's are Deluno's own.
  expect(byLevel.movie.some((field) => field.source === "server")).toBe(true);
  expect(byLevel.movie.some((field) => field.source === "deluno")).toBe(true);
  // A season and an episode are asked their show's catalogue questions (the network a show airs on, for one); a film is not.
  const ids = (level: string) => byLevel[level].map((field) => field.id);
  for (const level of ["show", "season", "episode"]) expect(ids(level), level).toContain("network");
  expect(ids("movie")).not.toContain("network");
  expect(ids("movie")).toContain("certification");
  // At least one field says which server kinds cannot answer it, so the drawer can warn.
  expect(byLevel.movie.some((field) => field.unsupportedOn.length > 0)).toBe(true);
  // The catalogue's own fields are never marked unavailable on a server.
  expect(byLevel.movie.filter((field) => field.source === "deluno").every((field) => field.unsupportedOn.length === 0)).toBe(true);
});
