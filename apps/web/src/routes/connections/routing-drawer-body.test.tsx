import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { DownloadClientItem, LibraryItem } from "../../lib/api";
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
