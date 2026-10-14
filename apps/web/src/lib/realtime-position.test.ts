import { beforeEach, describe, expect, it } from "vitest";
import { firstRealtimeInstance, firstRealtimePosition, forgetRealtimePosition, noteRealtimePosition } from "./realtime-position";

describe("realtime position (#1315)", () => {
  beforeEach(() => forgetRealtimePosition());

  it("keeps the first sequence with the server run it came from, and ignores later answers", () => {
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "41", "X-Deluno-Event-Instance": "run-a" }) });
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "99", "X-Deluno-Event-Instance": "run-b" }) });

    expect(firstRealtimePosition()).toBe(41);
    expect(firstRealtimeInstance()).toBe("run-a");
  });

  it("holds an empty run name when the answer does not carry one, and forgets both together", () => {
    noteRealtimePosition({ headers: new Headers({ "X-Deluno-Event-Seq": "7" }) });
    expect(firstRealtimeInstance()).toBe("");

    forgetRealtimePosition();
    expect(firstRealtimePosition()).toBe(0);
    expect(firstRealtimeInstance()).toBe("");
  });
});
