import { describe, expect, it } from "vitest";
import { cn, formatBytesFromGb } from "./utils";

describe("joining class names", () => {
  it("keeps the ones that apply and drops the ones that do not", () => {
    const off: boolean = Number("0") === 1;
    expect(cn("a", off && "b", undefined, null, "c")).toBe("a c");
  });

  it("lets a later Tailwind class win over an earlier one that sets the same thing", () => {
    expect(cn("p-2 text-sm", "p-4")).toBe("text-sm p-4");
  });
});

describe("a size in gigabytes", () => {
  it("shows one decimal place", () => {
    expect(formatBytesFromGb(12.345)).toBe("12.3 GB");
    expect(formatBytesFromGb(0)).toBe("0.0 GB");
  });

  it("says Unknown when there is no size, rather than NaN", () => {
    expect(formatBytesFromGb(null)).toBe("Unknown");
    expect(formatBytesFromGb(undefined)).toBe("Unknown");
    expect(formatBytesFromGb(Number.NaN)).toBe("Unknown");
    expect(formatBytesFromGb(Number.POSITIVE_INFINITY)).toBe("Unknown");
  });
});
