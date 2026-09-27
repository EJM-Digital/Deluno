import { describe, expect, it } from "vitest";
import { detectReleaseLanguage } from "./release-language";

describe("detectReleaseLanguage", () => {
  it("reads MULTI as Multilingual", () => {
    expect(detectReleaseLanguage("Movie.2026.MULTI.1080p.WEB-DL")).toBe("Multilingual");
  });

  it("reads VOSTFR as French", () => {
    expect(detectReleaseLanguage("Movie.2026.VOSTFR.1080p.WEB-DL")).toBe("French");
  });

  it("reads a plain English release name as English", () => {
    expect(detectReleaseLanguage("Movie.2026.English.1080p.WEB-DL")).toBe("English");
  });

  it("returns null when nothing in the name says a language", () => {
    expect(detectReleaseLanguage("Movie.2026.1080p.WEB-DL-GROUP")).toBeNull();
  });

  it("does not match a substring inside another word", () => {
    // "vf2" is a real French token, but must not match e.g. a release group like "TeamVF2X".
    expect(detectReleaseLanguage("Movie.2026.1080p-EVF2")).toBeNull();
  });
});
