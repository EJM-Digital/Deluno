import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FULL_DECORATIONS_MIN_PX, posterSize } from "./layout";
import { LIBRARY_HUES, Shelf } from "./shelf";
import { NO_TITLES } from "./titles";

// The shelf's box: tall enough for captions and wide enough for five posters at the stated width.
let box = { width: 900, height: 330 };
vi.mock("./hooks", async (importOriginal) => {
  const { createRef } = await import("react");
  return { ...(await importOriginal<typeof import("./hooks")>()), useElementSize: () => [createRef(), box] };
});

afterEach(cleanup);

describe("posterSize", () => {
  it("is small under 64px, full from 64px, and full while unmeasured", () => {
    expect(FULL_DECORATIONS_MIN_PX).toBe(64);
    expect(posterSize(63)).toBe("small");
    expect(posterSize(64)).toBe("full");
    expect(posterSize(undefined)).toBe("full");
    expect(posterSize(null)).toBe("full");
  });
});

describe("the shelf's small posters", () => {
  const item = (key: string, extra: object = {}) => ({ key, entity: { kind: "movie", id: key }, fallbackTitle: `Film ${key} (2001)`, release: null, quality: "Bluray 1080p", previousQuality: "WEB 720p", upgraded: true, sizeBytes: null, at: new Date().toISOString(), requestedBy: null, to: `/movies/${key}`, qualityMet: false, libraryId: "kids", libraryName: "Kids", ...extra }) as never;
  const counts = (libraryId: string, libraryName: string) => ({ libraryId, libraryName, mediaType: "movies", met: 1, upgradable: 1, downloading: 0, missing: 0, arrivedToday: 1 });
  const section = { movies: { qualityMet: 1, upgradable: 1, downloading: 0, missing: 0 }, shows: { qualityMet: 0, upgradable: 0, downloading: 0, missing: 0 }, libraryCounts: [counts("hd", "Movies HD"), counts("kids", "Kids")] } as never;
  const show = () => render(<MemoryRouter><Shelf items={[item("2")]} titles={NO_TITLES} newest={new Set()} heading="1 arrived today" library={section} /></MemoryRouter>);

  it("a small poster says the upgrade in its tooltip and name, and its tag is a strip link with the library's name", () => {
    box = { width: 340, height: 200 };
    show();
    const li = document.querySelector<HTMLElement>(".lsh-poster")!;
    expect(li).toHaveAttribute("data-size", "small");
    expect(li.querySelector(".lsh-up")).toBeNull();
    expect(li.querySelector(".lsh-art")!.children).toHaveLength(1);
    const poster = screen.getByRole("link", { name: /^Film 2/ });
    expect(poster).toHaveAttribute("title", expect.stringContaining("upgraded from WEB 720p"));
    expect(poster.getAttribute("aria-label")).toMatch(/upgraded from WEB 720p, .+/);
    const tag = screen.getByRole("link", { name: "Kids" });
    expect(tag).toHaveClass("lsh-tag");
    expect(tag).toHaveAttribute("href", "/movies?library=kids");
    expect(tag).toHaveAttribute("title", "Open Kids in the library");
    expect(tag.style.getPropertyValue("--tile-hue")).toBe(String(LIBRARY_HUES[1]));
  });

  it("a full poster has its whole tag and nothing else on the art, and says the upgrade in its status line", () => {
    box = { width: 900, height: 330 };
    show();
    const li = document.querySelector<HTMLElement>(".lsh-poster")!;
    expect(li).toHaveAttribute("data-size", "full");
    expect(li.querySelector(".lsh-up")).toBeNull();
    expect(li.querySelector(".lsh-art")!.children).toHaveLength(1);
    expect(li.querySelector(".lsh-status")).toHaveTextContent("Upgraded · Bluray 1080p");
    expect(screen.getByRole("link", { name: "Kids" })).toHaveAttribute("title", "Open Kids in the library");
  });
});
