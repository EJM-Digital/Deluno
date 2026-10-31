import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { RecoveryCodesPanel } from "./recovery-codes-panel";

const codes = Array.from({ length: 10 }, (_, n) => `ABCD-${String(n).padStart(4, "0")}`);

afterEach(() => vi.restoreAllMocks());

describe("the recovery codes can be saved as a text file (#1456)", () => {
  it("downloads deluno-recovery-codes.txt holding every code and a line saying each works once", async () => {
    let saved: Blob | null = null;
    const create = vi.spyOn(URL, "createObjectURL").mockImplementation((blob) => { saved = blob as Blob; return "blob:codes"; });
    const revoke = vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
    let clicked: HTMLAnchorElement | null = null;
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) { clicked = this; });
    render(<RecoveryCodesPanel codes={codes} onSaved={() => undefined} />);

    fireEvent.click(screen.getByRole("button", { name: "Download as a text file" }));

    expect(create).toHaveBeenCalledOnce();
    expect(clicked!.download).toBe("deluno-recovery-codes.txt");
    expect(clicked!.href).toBe("blob:codes");
    const text = await saved!.text();
    for (const code of codes) expect(text).toContain(code);
    expect(text).toContain("Each code works once.");
    expect(revoke).toHaveBeenCalledWith("blob:codes");
  });
});
