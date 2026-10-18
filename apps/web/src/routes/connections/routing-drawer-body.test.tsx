import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { DownloadClientItem, IndexerItem, LibraryItem } from "../../lib/api";
import { RoutingDrawerBody } from "./routing-drawer-body";


const library = { id: "lib-1", name: "Movies", mediaType: "movies" } as LibraryItem;
const client = (id: string, name: string, protocol = "qbittorrent") => ({ id, name, protocol, isEnabled: true, healthStatus: "healthy", moviesCategory: "movies", tvCategory: "tv" }) as DownloadClientItem;

/** #1136: the category explanation is said once, above the client list, not under every client. */
describe("RoutingDrawerBody", () => {
  it("says the category explanation once however many clients are switched on", () => {
    const clients = [client("a", "qBittorrent"), client("b", "SABnzbd", "sabnzbd"), client("c", "Deluge", "deluge")];
    render(
      <RoutingDrawerBody
        library={library}
        indexers={[]}
        clients={clients}
        sources={[]}
        targets={["a", "b", "c"]}
        categories={{}}
        sourceTags={{}}
        onSourceTagsChange={() => {}}
        categoryChecks={{}}
        onToggleSource={() => {}}
        onToggleClient={() => {}}
        onCategoryChange={() => {}}
        onCheckCategory={() => {}}
        busy={null}
      />
    );
    expect(screen.getAllByText(/not a folder path/)).toHaveLength(1);
    expect(screen.getAllByRole("button", { name: "Check category" })).toHaveLength(3);
    expect(screen.getAllByText("Using client default")).toHaveLength(3);
  });
});

const indexer = (id: string, name: string, mediaScope: string | null, healthStatus = "healthy") =>
  ({ id, name, protocol: "torznab", mediaScope, priority: 25, isEnabled: true, healthStatus }) as IndexerItem;

function renderRouting(over: { library?: LibraryItem; indexers: IndexerItem[]; sources?: string[]; onToggleSource?: (id: string, on: boolean) => void; onSourceTagsChange?: (id: string, patch: { required?: string; excluded?: string }) => void }) {
  return render(
    <RoutingDrawerBody
      library={over.library ?? library}
      indexers={over.indexers}
      clients={[]}
      sources={over.sources ?? []}
      targets={[]}
      categories={{}}
      sourceTags={{}}
      onSourceTagsChange={() => {}}
      categoryChecks={{}}
      onToggleSource={over.onToggleSource ?? (() => {})}
      onToggleClient={() => {}}
      onCategoryChange={() => {}}
      onCheckCategory={() => {}}
      busy={null}
    />
  );
}

/** A library searches only the indexers that can search its kind of title. */
describe("RoutingDrawerBody indexers", () => {
  const all = [indexer("m", "Movies only", "movies"), indexer("t", "TV only", "tv"), indexer("b", "Both kinds", "both"), indexer("u", "Unsaid scope", null)];

  it("lists, for a movie library, the movie and both-kind indexers (an unsaid scope reads as both), each with its health chip and a switch, and counts the ones switched on", () => {
    renderRouting({ indexers: all, sources: ["m"] });

    expect(screen.getByText("1 of 3 · only movie-capable indexers are listed")).toBeInTheDocument();
    for (const name of ["Movies only", "Both kinds", "Unsaid scope"]) expect(screen.getByText(name)).toBeInTheDocument();
    expect(screen.queryByText("TV only")).not.toBeInTheDocument();
    expect(screen.getAllByText("Healthy")).toHaveLength(3);
    expect(screen.getByRole("switch", { name: /Movies only/ })).toBeChecked();
    expect(screen.getByRole("switch", { name: /Both kinds/ })).not.toBeChecked();
  });

  it("lists, for a TV library, the TV and both-kind indexers and leaves the movie-only one out", () => {
    renderRouting({ library: { id: "lib-2", name: "Shows", mediaType: "tv" } as LibraryItem, indexers: all });

    expect(screen.getByText("0 of 3 · only TV-capable indexers are listed")).toBeInTheDocument();
    expect(screen.getByText("TV only")).toBeInTheDocument();
    expect(screen.queryByText("Movies only")).not.toBeInTheDocument();
  });

  it("switching one on tells the page which indexer, and warns when none is on", () => {
    const toggled: Array<[string, boolean]> = [];
    renderRouting({ indexers: all, onToggleSource: (id, on) => toggled.push([id, on]) });
    expect(screen.getByText(/No indexers selected/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("switch", { name: /Both kinds/ }));

    expect(toggled).toEqual([["b", true]]);
  });

  it("says there is no capable indexer yet when the only ones are for the other kind of title", () => {
    renderRouting({ indexers: [indexer("t", "TV only", "tv")] });

    expect(screen.getByText(/No movie-capable indexers yet/)).toBeInTheDocument();
  });

  it("offers the two tag choices under a switched-on indexer only, one pair for each indexer that is on", () => {
    const off = renderRouting({ indexers: all, sources: [] });
    expect(screen.queryByText("Only for titles tagged (optional)")).not.toBeInTheDocument();
    off.unmount();

    renderRouting({ indexers: all, sources: ["b"] });
    expect(screen.getAllByText("Only for titles tagged (optional)")).toHaveLength(1);
    expect(screen.getAllByText("Never for titles tagged (optional)")).toHaveLength(1);
    expect(screen.getByLabelText("Required tags for Both kinds")).toBeInTheDocument();
    expect(screen.getByLabelText("Excluded tags for Both kinds")).toBeInTheDocument();
  });
});
