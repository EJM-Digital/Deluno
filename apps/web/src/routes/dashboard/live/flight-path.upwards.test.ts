import { describe, expect, it } from "vitest";
import { poseAt } from "./flight-path";

const FROM = { left: 300, top: 400, width: 80, height: 120 };

describe("a poster flying up the screen", () => {
  it("bows to the same side of its path as one flying down, rather than flipping over", () => {
    const up = poseAt(0.5, FROM, { left: 100, top: 40, width: 40 });
    const down = poseAt(0.5, { left: 100, top: 40, width: 40, height: 60 }, { left: 300, top: 400, width: 80 });
    const straightMiddle = 200;

    // Flying up and to the left, or down and to the right, the bow goes rightwards of the straight line's middle.
    expect(up.x).toBeGreaterThan(straightMiddle);
    expect(down.x).toBeGreaterThan(straightMiddle);
  });

  it("stays on the straight line when there is nowhere to fly to", () => {
    const stay = poseAt(0.5, FROM, { left: 300, top: 400, width: 80 });
    expect(stay.x).toBeCloseTo(300 - (80 * 0.04) / 2, 5);
    expect(stay.y).toBeCloseTo(400 - (120 * 0.04) / 2, 5);
    expect(stay.scale).toBeCloseTo(1.04, 5);
  });

  it("bows up the screen when it flies straight right along a flat line", () => {
    const across = poseAt(0.5, { left: 0, top: 100, width: 40, height: 60 }, { left: 200, top: 100, width: 40 });
    expect(across.y).toBeLessThan(90);
  });

  it("bows down the screen when it flies straight left along a flat line", () => {
    const across = poseAt(0.5, { left: 200, top: 100, width: 40, height: 60 }, { left: 0, top: 100, width: 40 });
    expect(across.y).toBeGreaterThan(100);
  });
});
