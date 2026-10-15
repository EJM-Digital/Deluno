import { configure, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The tags row on a title's page (coverage register, areas movie-detail and show-detail): it lists the title's tags or says none are
 * assigned, Add or Edit opens the picker, Save posts the bulk tags route with the title's id and the tidied list, Cancel puts the
 * old tags back, and a failed save says so.
 */
const mocks = vi.hoisted(() => ({ fetchJson: vi.fn(), authedFetch: vi.fn() }));
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: mocks.fetchJson }));
vi.mock("../../lib/use-auth", () => ({ authedFetch: mocks.authedFetch }));

const { TitleTagsEditor } = await import("./title-tags-editor");

let known: Array<{ tagId: string; name: string }>;
let saved: Array<{ url: string; body: any }>;
const CATALOGUE = [{ id: "a", name: "kids", color: "slate", description: "", createdUtc: "", updatedUtc: "" }, { id: "b", name: "4k", color: "slate", description: "", createdUtc: "", updatedUtc: "" }];
const chooseTag = async (name: string) => fireEvent.click(await screen.findByRole("button", { name: `Add ${name} to Title tags` }));
const typeInPicker = (text: string) => fireEvent.change(screen.getByRole("textbox", { name: "Title tags" }), { target: { value: text } });

beforeEach(() => {
  known = [];
  saved = [];
  mocks.fetchJson.mockReset().mockImplementation(async (path: string) => (path === "/api/tags" ? CATALOGUE : path === "/api/tags/usage" ? [] : path.endsWith("/tags") ? known : []));
  mocks.authedFetch.mockReset().mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    saved.push({ url: String(input), body: JSON.parse(String(init?.body)) });
    return new Response("{}", { status: 200 });
  });
});

describe("the tags row", () => {
  it("says None assigned and offers Add when the title has no tags", async () => {
    render(<TitleTagsEditor id="m1" mediaType="movies" metadataJson={null} />);

    expect(await screen.findByText("None assigned")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add" })).toBeInTheDocument();
  });

  it("lists the tags the catalogue holds for the title and offers Edit", async () => {
    known = [{ tagId: "t1", name: "kids" }, { tagId: "t2", name: "4k" }];
    render(<TitleTagsEditor id="m1" mediaType="movies" metadataJson={null} />);

    expect(await screen.findByText("kids")).toBeInTheDocument();
    expect(screen.getByText("4k")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument();
    expect(mocks.fetchJson).toHaveBeenCalledWith("/api/movies/m1/tags");
  });

  it("a show reads its tags from the series route", async () => {
    render(<TitleTagsEditor id="s1" mediaType="series" metadataJson={null} />);

    await waitFor(() => expect(mocks.fetchJson).toHaveBeenCalledWith("/api/series/s1/tags"));
  });

  it("falls back to the tags in the stored metadata when the catalogue route cannot be read", async () => {
    mocks.fetchJson.mockRejectedValue(new Error("down"));
    render(<TitleTagsEditor id="m1" mediaType="movies" metadataJson={JSON.stringify({ Tags: ["from-metadata"] })} />);

    expect(await screen.findByText("from-metadata")).toBeInTheDocument();
  });

  it("Save posts the film's id and the chosen tags to the movies bulk route and tells the page", async () => {
    const onSaved = vi.fn();
    render(<TitleTagsEditor id="m1" mediaType="movies" metadataJson={null} onSaved={onSaved} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add" }));
    typeInPicker("k");
    await chooseTag("kids");
    typeInPicker("4");
    await chooseTag("4k");

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0].url).toBe("/api/movies/bulk/tags");
    expect(saved[0].body).toEqual({ movieIds: ["m1"], tags: "kids, 4k" });
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
  });

  it("tidies a stored list that repeats a tag or leaves blanks, and Save sends it tidy", async () => {
    mocks.fetchJson.mockImplementation(async (path: string) => { if (path.endsWith("/tags") && path !== "/api/tags") throw new Error("down"); return path === "/api/tags" ? CATALOGUE : []; });
    render(<TitleTagsEditor id="m1" mediaType="movies" metadataJson={JSON.stringify({ tags: "kids; Kids,, 4k" })} />);
    expect(await screen.findByText("kids")).toBeInTheDocument();
    expect(screen.getAllByText(/^kids$/i)).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0].body).toEqual({ movieIds: ["m1"], tags: "kids, 4k" });
  });

  it("a show saves with seriesIds to the series bulk route", async () => {
    render(<TitleTagsEditor id="s1" mediaType="series" metadataJson={null} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add" }));
    typeInPicker("k");
    await chooseTag("kids");

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0].url).toBe("/api/series/bulk/tags");
    expect(saved[0].body).toEqual({ seriesIds: ["s1"], tags: "kids" });
  });

  it("Cancel throws the edit away and shows the old tags again", async () => {
    known = [{ tagId: "t1", name: "kids" }];
    render(<TitleTagsEditor id="m1" mediaType="movies" metadataJson={null} />);
    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    typeInPicker("4");
    await chooseTag("4k");

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.getByText("kids")).toBeInTheDocument();
    expect(screen.queryByText("4k")).toBeNull();
    expect(saved).toHaveLength(0);
  });

  it("a failed save says Tags could not be saved. and keeps the editor open", async () => {
    mocks.authedFetch.mockResolvedValue(new Response("{}", { status: 500 }));
    render(<TitleTagsEditor id="m1" mediaType="movies" metadataJson={null} />);
    fireEvent.click(await screen.findByRole("button", { name: "Add" }));
    typeInPicker("k");
    await chooseTag("kids");

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Tags could not be saved.");
    expect(screen.getByRole("button", { name: "Save" })).toBeInTheDocument();
  });
});
