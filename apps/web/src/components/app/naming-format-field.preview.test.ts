import { describe, expect, it } from "vitest";
import { previewNamingFormat } from "./naming-format-field";

describe("previewNamingFormat's sample for every token (#1329)", () => {
  it.each([
    ["{Movie Title}", "Arrival"],
    ["{Release Year}", "2016"],
    ["{IMDb ID}", "tt2543164"],
    ["{Quality Profile}", "HD"],
    ["{Series Title}", "Severance"],
    ["{Series Year}", "2022"],
    ["{TVDb ID}", "371980"],
    ["{Network}", "Apple TV+"],
    ["{Season:00}", "01"],
    ["{season:00}", "01"],
    ["{Episode:00}", "01"],
    ["{episode:00}", "01"],
    ["{Episode Title}", "Good News About Hell"],
    ["{Quality}", "WEB 1080p"],
    ["{Custom Formats}", "AMZN"],
    ["{Release Group}", "FLUX"],
    ["{Edition}", "IMAX"],
    ["{Genre}", "Sci-Fi"],
    ["{Tag}", "premium"],
    ["{Air-Date}", "2022-09-30"],
    ["{Air Date}", "2022-09-30"],
    ["{absolute:000}", "042"],
    ["{Original Title}", "El Laberinto del Fauno"],
    ["{Original Filename}", "arrival.2016.1080p.web-dl-flux"],
    ["{Release Hash}", "a1b2c3d4"],
    ["{MediaInfo VideoCodec}", "H.264"],
    ["{MediaInfo AudioCodec}", "DTS-HD"],
    ["{MediaInfo AudioChannels}", "5.1"],
    ["{MediaInfo VideoDynamicRangeType}", "HDR10"],
    ["{MediaInfo VideoBitDepth}", "10-bit"]
  ])("%s reads %s", (token, sample) => {
    expect(previewNamingFormat(token)).toBe(sample);
    expect(previewNamingFormat(`before ${token} after`)).toBe(`before ${sample} after`);
  });

  it("fills every occurrence, leaves the text between tokens and any token it does not know exactly as typed", () => {
    expect(previewNamingFormat("{Movie Title} ({Release Year}) - {Movie Title} {Something Else}")).toBe("Arrival (2016) - Arrival {Something Else}");
    expect(previewNamingFormat("")).toBe("");
    expect(previewNamingFormat("no tokens here")).toBe("no tokens here");
  });
});
