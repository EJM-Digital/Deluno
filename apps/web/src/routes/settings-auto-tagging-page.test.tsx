import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch: authedFetchMock }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../components/app/tag-picker", () => ({
  TagPicker: ({ label, value, onChange }: { label?: string; value: string; onChange: (value: string) => void }) => (
    <input aria-label={label} value={value} onChange={(event) => onChange(event.target.value)} />
  ),
  parseTagList: (raw: string | null | undefined) => (raw ?? "").split(",").map((item) => item.trim()).filter(Boolean),
  joinTagList: (tags: string[]) => tags.join(", ")
}));

const loaderData = vi.hoisted(() => ({
  libraries: [{ id: "l1", name: "Anime", rootPath: "D:\\Anime", mediaType: "tv" }],
  qualityProfiles: [{ id: "p1", name: "HD-1080p" }],
  rules: [] as unknown[]
}));
const revalidateMock = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsAutoTaggingPage } = await import("./settings-auto-tagging-page");

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsAutoTaggingPage />
    </MemoryRouter>
  );
}

/** #1024: no rules on a new install; a rule is tags plus conditions, read back in words. */
describe("Auto-tagging page (#1024)", () => {
  beforeEach(() => {
    authedFetchMock.mockReset();
    authedFetchMock.mockResolvedValue({ ok: true, status: 201, json: async () => ({ id: "r1" }) });
    loaderData.rules = [];
  });

  it("says nothing is tagged on its own until a rule is added", () => {
    renderPage();
    expect(screen.getByText("No auto-tagging rules")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Run the rules now" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("reads a rule back in words with the tags it gives", () => {
    loaderData.rules = [
      {
        id: "r1",
        name: "Anime",
        tags: ["anime"],
        removeAutomatically: true,
        appliesTo: "both",
        isEnabled: true,
        specifications: [
          { type: "genre", negate: false, value: "Animation" },
          { type: "originalLanguage", negate: false, value: "ja" }
        ],
        createdUtc: "",
        updatedUtc: ""
      }
    ];
    renderPage();
    expect(screen.getByText("Genre is Animation and Original language is ja")).toBeTruthy();
    expect(screen.getByText("Takes the tags back when a title stops matching")).toBeTruthy();
    expect(screen.getByRole("switch", { name: "Pause Anime" })).toBeTruthy();
  });

  it("refuses to save a rule with no name or tag, and saves one that has both", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    fireEvent.change(screen.getByLabelText("Condition 1 value"), { target: { value: "Animation" } });
    fireEvent.click(screen.getByRole("button", { name: "Create rule" }));
    expect(await screen.findByText("Give the rule a name.")).toBeTruthy();
    expect(await screen.findByText("Choose at least one tag for the rule to give.")).toBeTruthy();
    expect(authedFetchMock).not.toHaveBeenCalled();

    fireEvent.change(screen.getByPlaceholderText("Example: anime"), { target: { value: "Cartoons" } });
    fireEvent.change(screen.getByLabelText("Tags to give"), { target: { value: "cartoons" } });
    fireEvent.click(screen.getByRole("button", { name: "Create rule" }));

    await vi.waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    const [url, init] = authedFetchMock.mock.calls[0] as [string, { method: string; body: string }];
    expect(url).toBe("/api/auto-tag-rules");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body)).toMatchObject({
      name: "Cartoons",
      tags: ["cartoons"],
      removeAutomatically: false,
      appliesTo: "both",
      specifications: [{ type: "genre", negate: false, value: "Animation" }]
    });
  });

  it("keeps a condition's 'is not' and offers every condition a rule can use", () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "New rule" }));
    fireEvent.change(screen.getByLabelText("Condition 1 is or is not"), { target: { value: "not" } });
    expect((screen.getByLabelText("Condition 1 is or is not") as HTMLSelectElement).value).toBe("not");
    const kinds = [...(screen.getByLabelText("Condition 1 kind") as HTMLSelectElement).options].map((option) => option.textContent);
    expect(kinds).toEqual(expect.arrayContaining(["Genre", "Monitored", "Original language", "Quality profile", "Root folder", "Series type", "Status", "Tag", "Year", "Keyword", "Runtime (minutes)", "Studio or network", "Library", "Anime", "Certification", "Requested in 4K", "Requested through Seerr", "Added by a list or collection"]));
  });
});
