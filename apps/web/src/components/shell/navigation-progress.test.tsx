import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * #1081: a click always answers within a frame. While the next screen is being
 * opened a slim bar runs across the top; when nothing is opening there is none.
 */
const navigation = vi.hoisted(() => ({ state: "idle" as "idle" | "loading" | "submitting" }));
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useNavigation: () => ({ state: navigation.state })
}));

const { NavigationProgress } = await import("./navigation-progress");

describe("NavigationProgress", () => {
  afterEach(() => { navigation.state = "idle"; });

  it("shows nothing while no screen is being opened", () => {
    render(<NavigationProgress />);
    expect(screen.queryByRole("progressbar")).toBeNull();
  });

  it("runs while a screen is being opened", () => {
    navigation.state = "loading";
    render(<NavigationProgress />);
    expect(screen.getByRole("progressbar", { name: "Opening the next screen" })).toBeInTheDocument();
  });

  it("runs while a form is being submitted too", () => {
    navigation.state = "submitting";
    render(<NavigationProgress />);
    expect(screen.getByRole("progressbar")).toBeInTheDocument();
  });
});
