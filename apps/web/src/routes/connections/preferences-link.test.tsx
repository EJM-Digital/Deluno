import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import {
  CLOUDFLARE_HELPER_PLACE,
  MessageWithPreferencesLink,
  PreferencesLink,
  RoutingFixLink,
  mentionsCloudflare
} from "./preferences-link";

const inRouter = (node: React.ReactNode) => render(<MemoryRouter>{node}</MemoryRouter>);

describe("the link to Preferences, General", () => {
  it("is named Open Preferences, General unless the screen names it otherwise", () => {
    const { unmount } = inRouter(<PreferencesLink />);
    expect(screen.getByRole("link", { name: "Open Preferences, General" })).toHaveAttribute("href", "/settings/general");
    unmount();
    inRouter(<PreferencesLink>Set the helper</PreferencesLink>);
    expect(screen.getByRole("link", { name: "Set the helper" })).toHaveAttribute("href", "/settings/general");
  });

  it("is added after a message only when the message names that page", () => {
    const { unmount } = inRouter(<MessageWithPreferencesLink text={`Set the helper in ${CLOUDFLARE_HELPER_PLACE}.`} />);
    expect(screen.getByRole("link", { name: "Open Preferences, General" })).toBeInTheDocument();
    unmount();
    inRouter(<MessageWithPreferencesLink text="The tracker refused the login." />);
    expect(screen.getByText("The tracker refused the login.")).toBeInTheDocument();
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("recognises a Cloudflare message in any case, and nothing for a blank one", () => {
    expect(mentionsCloudflare("Blocked by CLOUDFLARE")).toBe(true);
    expect(mentionsCloudflare("Connection refused")).toBe(false);
    expect(mentionsCloudflare("")).toBe(false);
    expect(mentionsCloudflare(null)).toBe(false);
    expect(mentionsCloudflare(undefined)).toBe(false);
  });
});

describe("the fix link on a row nothing uses", () => {
  it("goes to library routing without also opening the row", () => {
    const onRowClick = vi.fn();
    inRouter(<div onClick={onRowClick}><RoutingFixLink>Choose a library</RoutingFixLink></div>);
    const link = screen.getByRole("link", { name: "Choose a library" });
    expect(link).toHaveAttribute("href", "/indexers/library-routing");
    fireEvent.click(link);
    expect(onRowClick).not.toHaveBeenCalled();
  });
});
