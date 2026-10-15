import { describe, expect, it } from "vitest";
import { safeReturnAddress } from "./return-address";

describe("safeReturnAddress follows only a path on this app (#1339)", () => {
  it.each([
    ["//host"],
    ["//host/path"],
    ["/\\host"],
    ["/\\/host"],
    ["https://host"],
    ["http://host/x"],
    ["HTTPS://host"],
    ["//"],
    ["%2F%2Fhost"],
    ["/%5Chost"],
    ["/%5chost"],
    ["/%2F%2Fhost"],
    ["/%2fhost"],
    ["javascript:alert(1)"],
    ["JaVaScRiPt:alert(1)"],
    ["data:text/html,hi"],
    ["\\\\host"],
    ["\\host"],
    ["/\t/host"],
    ["/\n/host"],
    ["/\r/host"],
    ["/\u0000/host"],
    ["host"],
    ["movies"],
    [" /movies"],
    [" //host"],
    ["/a\\b"],
    ["/%"],
    [""],
  ])("refuses %j and falls back to the home page", (value) => {
    expect(safeReturnAddress(value)).toBe("/");
  });

  it("falls back to what the caller names", () => {
    expect(safeReturnAddress("//host", "/login")).toBe("/login");
    expect(safeReturnAddress(null, "/login")).toBe("/login");
    expect(safeReturnAddress(undefined)).toBe("/");
  });

  it.each([
    ["/"],
    ["/movies"],
    ["/movies?filter=missing"],
    ["/movies?filter=missing&sort=title#top"],
    ["/tv/42/seasons?x=%2F%2Fnot-a-host"],
    ["/setup-guide?return=%2Fmovies"],
    ["/search-cycles?library=a%20b"],
    ["/movies#section"],
  ])("keeps the deep link %j exactly", (value) => {
    expect(safeReturnAddress(value)).toBe(value);
  });
});
