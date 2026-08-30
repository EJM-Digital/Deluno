/**
 * The credits row, and the reader under it.
 *
 * <p>The reader is the part worth pinning. It existed twice — once per detail
 * page — and both copies knew only about `cast`, so a page could never show a
 * crew even once the gateway started answering with one. It also has to read
 * two casings, because Deluno stores camelCase from the gateway and PascalCase
 * from its own record, and a reader that knows one of them returns an empty
 * list for half the installs while looking perfectly correct.</p>
 */
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { CreditsRow, readStoredCredits } from "./credits-row";

describe("readStoredCredits", () => {
  it("reads a camelCase blob, the shape the gateway answers with", () => {
    const { cast, crew } = readStoredCredits(JSON.stringify({
      cast: [{ personId: "1245", name: "Amy Adams", character: "Louise Banks", profileUrl: "/amy.jpg" }],
      crew: [{ personId: "137427", name: "Denis Villeneuve", job: "Director, Producer", profileUrl: "/dv.jpg" }]
    }));

    expect(cast).toEqual([{ name: "Amy Adams", role: "Louise Banks", profileUrl: "/amy.jpg", personId: "1245" }]);
    expect(crew).toEqual([{ name: "Denis Villeneuve", role: "Director, Producer", profileUrl: "/dv.jpg", personId: "137427" }]);
  });

  it("reads a PascalCase blob, the shape Deluno's own record serialises", () => {
    const { cast, crew } = readStoredCredits(JSON.stringify({
      Cast: [{ Name: "Amy Adams", Character: "Louise Banks", ProfileUrl: "/amy.jpg", PersonId: "1245" }],
      Crew: [{ Name: "Denis Villeneuve", Job: "Director", ProfileUrl: null }]
    }));

    expect(cast[0]).toEqual({ name: "Amy Adams", role: "Louise Banks", profileUrl: "/amy.jpg", personId: "1245" });
    expect(crew[0]).toEqual({ name: "Denis Villeneuve", role: "Director", profileUrl: null, personId: null });
  });

  it("answers with two empty lists rather than throwing on anything unusable", () => {
    expect(readStoredCredits(null)).toEqual({ cast: [], crew: [] });
    expect(readStoredCredits("not json at all")).toEqual({ cast: [], crew: [] });
    expect(readStoredCredits(JSON.stringify({ cast: "Amy Adams" }))).toEqual({ cast: [], crew: [] });
  });

  it("drops a nameless credit, and keeps one with no photo or role", () => {
    const { cast } = readStoredCredits(JSON.stringify({
      cast: [{ character: "Uncredited" }, { name: "  Tzi Ma  " }]
    }));

    expect(cast).toEqual([{ name: "Tzi Ma", role: null, profileUrl: null, personId: null }]);
  });
});

describe("CreditsRow", () => {
  it("draws nothing at all when there is nobody to draw", () => {
    const { container } = render(<CreditsRow heading="Crew" people={[]} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("shows every person it is given, not a slice of them", () => {
    // Ten was the cap the gateway used to apply and the page used to re-apply.
    const people = Array.from({ length: 24 }, (_, index) => ({
      name: `Player ${index + 1}`,
      role: `Role ${index + 1}`,
      profileUrl: null,
      personId: null
    }));

    render(<CreditsRow heading="Cast" people={people} />);

    expect(screen.getByText("24 credited")).toBeInTheDocument();
    expect(screen.getByText("Player 24")).toBeInTheDocument();
  });

  it("scrolls sideways rather than wrapping into a block you have to parse", () => {
    const { container } = render(
      <CreditsRow heading="Cast" people={[{ name: "Amy Adams", role: "Louise Banks", profileUrl: "/amy.jpg", personId: null }]} />
    );

    const row = container.querySelector(".overflow-x-auto");
    expect(row).not.toBeNull();
    expect(row?.className).not.toContain("flex-wrap");
    expect(container.querySelector("figure")?.className).toContain("shrink-0");
  });
});

describe("CreditsRow arrows", () => {
  it("offers both directions, and starts with only the forward one live", () => {
    // jsdom gives every element a zero layout, so clientWidth === scrollWidth
    // and neither end has more to show. That is the honest reading of "nothing
    // overflows", and it is what a short row does in a browser too.
    render(<CreditsRow heading="Cast" people={[{ name: "Amy Adams", role: "Louise Banks", profileUrl: null, personId: null }]} />);

    expect(screen.getByLabelText("Scroll cast left")).toBeDisabled();
    expect(screen.getByLabelText("Scroll cast right")).toBeDisabled();
  });

  it("keeps native scrolling and hides the bar, rather than replacing one with the other", () => {
    // Arrows are the signal; the scrolling is how people actually move the row
    // — trackpad, touch, and arrow keys after tabbing in. Losing it to a pair
    // of buttons would be a downgrade wearing a nicer coat.
    const { container } = render(
      <CreditsRow heading="Crew" people={[{ name: "Denis Villeneuve", role: "Director", profileUrl: null, personId: null }]} />
    );

    const row = container.querySelector(".overflow-x-auto");
    expect(row).not.toBeNull();
    expect(row?.className).toContain("no-scrollbar");
  });
});

describe("a credit as a link", () => {
  it("links a person we can identify, and leaves one we cannot as a plain card", () => {
    // A face with a name under it invites a click. A credit stored before the
    // person id was read has nothing to link to, so it must not pretend — a
    // link to the wrong person is worse than no link.
    const { container } = render(
      <CreditsRow
        heading="Cast"
        people={[
          { name: "Amy Adams", role: "Louise Banks", profileUrl: null, personId: "1245" },
          { name: "Someone Older", role: "Extra", profileUrl: null, personId: null }
        ]}
      />
    );

    const links = container.querySelectorAll("a");
    expect(links).toHaveLength(1);
    expect(links[0].getAttribute("href")).toBe("https://www.themoviedb.org/person/1245");
    // Opened away from Deluno, and without handing the destination a referrer
    // window it could drive.
    expect(links[0].getAttribute("rel")).toContain("noreferrer");
    expect(container.querySelectorAll("figure")).toHaveLength(2);
  });
});
