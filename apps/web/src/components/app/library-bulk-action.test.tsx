import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { BulkAction } from "./library-bulk-action";

describe("a bulk action button", () => {
  it("runs its action when pressed, in every look", () => {
    const onClick = vi.fn();
    for (const variant of ["ghost", "primary", "danger"] as const) {
      const { unmount } = render(<BulkAction label={`Do ${variant}`} icon={<i data-testid="icon" />} onClick={onClick} variant={variant} />);
      fireEvent.click(screen.getByRole("button", { name: `Do ${variant}` }));
      expect(screen.getByTestId("icon")).toBeTruthy();
      unmount();
    }
    expect(onClick).toHaveBeenCalledTimes(3);
  });

  it("uses the quiet look when none is asked for", () => {
    render(<BulkAction label="Quiet" icon={<i />} onClick={() => undefined} />);
    expect(screen.getByRole("button", { name: "Quiet" }).className).toContain("media-muted-foreground");
  });

  it("swaps the icon for a spinner while loading, and cannot be pressed when disabled", () => {
    const onClick = vi.fn();
    render(<BulkAction label="Search" icon={<i data-testid="icon" />} onClick={onClick} loading disabled />);
    expect(screen.queryByTestId("icon")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    expect(onClick).not.toHaveBeenCalled();
  });
});
