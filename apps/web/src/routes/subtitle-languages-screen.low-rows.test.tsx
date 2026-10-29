import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api";

const loaderData = { libraries: [], languages: [], settings: { ...emptyPlatformSettingsSnapshot } };

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SubtitleLanguagesPage } = await import("./subtitle-languages-screen");

describe("Settings -> Subtitles (low rows)", () => {
  it("with no library yet it says subtitle languages are set per library, so make one under Media Management first", () => {
    render(<MemoryRouter><SubtitleLanguagesPage /></MemoryRouter>);

    expect(screen.getByText("No libraries yet")).toBeVisible();
    expect(screen.getByText("Subtitle languages are set per library, so make one under Media Management first.")).toBeVisible();
  });
});
