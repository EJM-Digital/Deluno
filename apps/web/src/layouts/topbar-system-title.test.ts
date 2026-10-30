import { describe, expect, it } from "vitest";
import { routeMeta } from "./app-layout";

describe("the top bar on a System page (#1329)", () => {
  it.each(["/system", "/system/audit", "/system/api", "/system/docs", "/system/security", "/system/backups", "/system/recycle-bin", "/system/updates"])("%s reads System with the one System description", (path) => {
    const meta = routeMeta.find((item) => item.match(path))!;
    const text = (value: string | ((path: string) => string)) => (typeof value === "function" ? value(path) : value);
    expect(text(meta.title)).toBe("System");
    expect(text(meta.subtitle)).toBe("How this installation is doing — health, backups, updates, and audit");
  });
});
