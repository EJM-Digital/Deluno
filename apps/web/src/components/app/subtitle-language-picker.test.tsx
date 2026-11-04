import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SubtitleLanguagePicker } from "./subtitle-language-picker";

afterEach(cleanup);

const options = [{ code: "en", name: "English" }, { code: "fr", name: "French" }, { code: "de", name: "German" }] as never[];
const show = (over: Partial<Parameters<typeof SubtitleLanguagePicker>[0]> = {}) => {
  const onChange = vi.fn();
  render(<SubtitleLanguagePicker languages={[]} mode="all" options={options} onChange={onChange} {...over} />);
  return onChange;
};
const adder = () => screen.getByRole("combobox", { name: "Add a subtitle language" }) as HTMLSelectElement;

describe("the subtitle language picker", () => {
  it("says it could not load the list when it has none, and offers nothing else", () => {
    show({ options: [] });
    expect(screen.getByText(/Deluno could not load the list of languages/)).toBeInTheDocument();
    expect(screen.queryByRole("combobox")).toBeNull();
  });

  it("says no subtitles are wanted when no language is chosen, and asks to choose one", () => {
    show();
    expect(screen.getByText("No subtitles wanted here. Deluno will not look for any, and titles on this shelf show no subtitle bar.")).toBeInTheDocument();
    expect(screen.queryByRole("list")).toBeNull();
    expect(Array.from(adder().options).map((option) => option.textContent)).toEqual(["Choose a language…", "English", "French", "German"]);
  });

  it("adds a language to the end of the list, keeping the mode", () => {
    const onChange = show({ languages: ["fr"], mode: "first" });
    fireEvent.change(adder(), { target: { value: "en" } });
    expect(onChange).toHaveBeenCalledWith({ languages: ["fr", "en"], mode: "first" });
  });

  it("offers only the languages not already wanted, and asks to add another", () => {
    show({ languages: ["fr"] });
    expect(Array.from(adder().options).map((option) => option.textContent)).toEqual(["Add another language…", "English", "German"]);
  });

  it("does nothing when the empty choice is picked", () => {
    const onChange = show({ languages: ["fr"] });
    fireEvent.change(adder(), { target: { value: "" } });
    expect(onChange).not.toHaveBeenCalled();
  });

  it("holds the adder when every language is already wanted", () => {
    show({ languages: ["en", "fr", "de"] });
    expect(adder()).toBeDisabled();
    cleanup();
    show({ languages: ["en"] });
    expect(adder()).not.toBeDisabled();
  });

  it("lists the wanted languages by name, with their order only when the first one found counts", () => {
    show({ languages: ["fr", "en"], mode: "first" });
    const items = within(screen.getByRole("list")).getAllByRole("listitem");
    expect(items.map((item) => item.textContent)).toEqual(["1French", "2English"]);
    cleanup();
    show({ languages: ["fr", "en"], mode: "all" });
    expect(within(screen.getByRole("list")).getAllByRole("listitem").map((item) => item.textContent)).toEqual(["French", "English"]);
  });

  it("shows a code it has no name for in capitals", () => {
    show({ languages: ["pt"] });
    expect(screen.getByRole("button", { name: "Stop wanting PT subtitles" })).toBeInTheDocument();
    expect(screen.getByText("Every file on this shelf gets PT subtitles.")).toBeInTheDocument();
  });

  it("stops wanting a language, keeping the rest in order and the mode", () => {
    const onChange = show({ languages: ["fr", "en", "de"], mode: "first" });
    fireEvent.click(screen.getByRole("button", { name: "Stop wanting English subtitles" }));
    expect(onChange).toHaveBeenCalledWith({ languages: ["fr", "de"], mode: "first" });
  });

  it("holds the removers and the mode while disabled", () => {
    show({ languages: ["fr", "en"], disabled: true });
    expect(screen.getByRole("button", { name: "Stop wanting French subtitles" })).toBeDisabled();
    expect(adder()).toBeDisabled();
    for (const radio of screen.getAllByRole("radio")) expect(radio).toBeDisabled();
    cleanup();
    show({ languages: ["fr", "en"] });
    expect(screen.getByRole("button", { name: "Stop wanting French subtitles" })).not.toBeDisabled();
    for (const radio of screen.getAllByRole("radio")) expect(radio).not.toBeDisabled();
  });

  it("asks how many languages a file needs only once there are two", () => {
    show({ languages: ["fr"] });
    expect(screen.queryByRole("radiogroup")).toBeNull();
    cleanup();
    show({ languages: ["fr", "en"], mode: "first" });
    const group = screen.getByRole("radiogroup", { name: "How many of these languages a file needs" });
    expect(within(group).getAllByRole("radio").map((radio) => [radio.textContent, radio.getAttribute("aria-checked")])).toEqual([["All of them", "false"], ["First one found", "true"]]);
  });

  it("changes the mode without changing the languages", () => {
    const onChange = show({ languages: ["fr", "en"], mode: "all" });
    fireEvent.click(screen.getByRole("radio", { name: "First one found" }));
    expect(onChange).toHaveBeenCalledWith({ languages: ["fr", "en"], mode: "first" });
  });

  it("can go back to all of them from the first one found", () => {
    const onChange = show({ languages: ["fr", "en"], mode: "first" });
    fireEvent.click(screen.getByRole("radio", { name: "All of them" }));
    expect(onChange).toHaveBeenCalledWith({ languages: ["fr", "en"], mode: "all" });
  });

  it("says what each mode means for the languages in order", () => {
    show({ languages: ["fr"], mode: "all" });
    expect(screen.getByText("Every file on this shelf gets French subtitles.")).toBeInTheDocument();
    cleanup();
    show({ languages: ["fr", "en", "de"], mode: "all" });
    expect(screen.getByText("Every file on this shelf gets French, English and German — all of them, on every file.")).toBeInTheDocument();
    cleanup();
    show({ languages: ["fr", "en", "de"], mode: "first" });
    expect(screen.getByText("Every file on this shelf gets French, or the next one on the list if French cannot be found. Just one, never two.")).toBeInTheDocument();
    cleanup();
    show({ languages: ["fr", "en"], mode: "all" });
    expect(screen.getByText("Every file on this shelf gets French and English — all of them, on every file.")).toBeInTheDocument();
  });
});
