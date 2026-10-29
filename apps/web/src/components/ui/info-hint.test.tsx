import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Field } from "./field";
import { Input } from "./input";
import { ListCard } from "./list-card";

/** #1430: one (i) icon, shared, that shows and hides a longer description. */

describe("the (i) on a Field", () => {
  it("shows no (i) and no note for a Field with no longer description", () => {
    render(<Field label="Port" help="Short."><Input /></Field>);

    expect(screen.queryByRole("button", { name: /More about/ })).toBeNull();
    expect(screen.queryByRole("note")).toBeNull();
  });

  it("starts hidden, shows the description when pressed and hides it when pressed again", async () => {
    const user = userEvent.setup();
    render(<Field label="Helper address" help="Short." info="The long way of saying it."><Input /></Field>);
    const icon = screen.getByRole("button", { name: "More about Helper address" });

    expect(icon).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("The long way of saying it.")).toBeNull();

    await user.click(icon);
    expect(icon).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("note")).toHaveTextContent("The long way of saying it.");
    expect(icon.getAttribute("aria-controls")).toBe(screen.getByRole("note").id);
    expect(screen.getByText("Short.")).toBeInTheDocument();

    await user.click(icon);
    expect(icon).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("note")).toBeNull();
  });

  it("keeps the label wired to its control and gives each (i) its own name", () => {
    render(<><Field label="One" info="a"><Input /></Field><Field label="Two" info="b"><Input /></Field></>);

    expect(screen.getAllByRole("textbox").map((box) => box.id).every(Boolean)).toBe(true);
    expect(screen.getByLabelText("One")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "More about One" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "More about Two" })).toBeInTheDocument();
  });

  it("names a Field whose label is not plain text 'this setting'", () => {
    render(<Field label={<span>Fancy</span>} info="a"><Input /></Field>);

    expect(screen.getByRole("button", { name: "More about this setting" })).toBeInTheDocument();
  });

  it("hides the (i) with the label when the label is hidden", () => {
    const { container } = render(<Field label="Quiet" hideLabel info="a"><Input /></Field>);

    expect(container.querySelector(".sr-only")).not.toBeNull();
  });
});

describe("the (i) on a ListCard", () => {
  it("toggles the section's description from beside its title", async () => {
    const user = userEvent.setup();
    render(<ListCard title="Tracker definitions" info="What this section does."><div>body</div></ListCard>);
    const icon = screen.getByRole("button", { name: "More about Tracker definitions" });

    expect(screen.queryByRole("note")).toBeNull();
    await user.click(icon);
    expect(screen.getByRole("note")).toHaveTextContent("What this section does.");
    expect(icon).toHaveAttribute("aria-expanded", "true");
    await user.click(icon);
    expect(screen.queryByRole("note")).toBeNull();
  });

  it("has no (i) without an info", () => {
    render(<ListCard title="Plain"><div>body</div></ListCard>);

    expect(screen.queryByRole("button", { name: /More about/ })).toBeNull();
  });

  it("names a card whose title is not plain text 'this section'", () => {
    render(<ListCard title={<em>Rich</em>} info="x"><div /></ListCard>);

    expect(screen.getByRole("button", { name: "More about this section" })).toBeInTheDocument();
  });
});
