import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

const dismiss = vi.fn();
let stack: { id: string }[] = [];

vi.mock("sonner", () => ({
  Toaster: (props: Record<string, unknown>) => <div data-testid="sonner" data-offset={JSON.stringify(props.offset)} />,
  toast: { dismiss },
  useSonner: () => ({ toasts: stack }),
}));

vi.mock("next-themes", () => ({ useTheme: () => ({ resolvedTheme: "dark" }) }));

const { Toaster } = await import("./toaster");

describe("toast stack", () => {
  it("offers no clear-all until there is more than one toast to clear", () => {
    stack = [];
    const { unmount } = render(<Toaster />);
    expect(screen.queryByRole("button", { name: /clear all/i })).toBeNull();
    unmount();

    stack = [{ id: "a" }];
    render(<Toaster />);
    expect(screen.queryByRole("button", { name: /clear all/i })).toBeNull();
  });

  it("clears the whole stack in one click, and says how many", async () => {
    stack = [{ id: "a" }, { id: "b" }, { id: "c" }];
    render(<Toaster />);

    const button = screen.getByRole("button", { name: /clear all 3/i });
    button.click();

    expect(dismiss).toHaveBeenCalledWith();
  });

  it("reserves a lane below the stack so the control cannot sit on top of a toast", () => {
    stack = [{ id: "a" }, { id: "b" }];
    render(<Toaster />);

    const offset = JSON.parse(screen.getByTestId("sonner").dataset.offset ?? "{}") as { bottom?: number };
    expect(offset.bottom).toBeGreaterThan(0);
  });
});
