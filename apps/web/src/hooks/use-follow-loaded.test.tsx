import { act, renderHook } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { useFollowLoaded } from "./use-follow-loaded";

function useDraft(loaded: { v: number }) {
  const [draft, setDraft] = useState(loaded);
  const [dirty, setDirty] = useState(false);
  useFollowLoaded(loaded, dirty, (next) => setDraft(next));
  return { draft, setDirty };
}

describe("a page's copy of what the loader read", () => {
  it("follows new loader data when the person has no unsaved edits", () => {
    const first = { v: 1 };
    const view = renderHook(({ loaded }) => useDraft(loaded), { initialProps: { loaded: first } });
    view.rerender({ loaded: { v: 2 } });
    expect(view.result.current.draft.v).toBe(2);
  });

  it("leaves the person's unsaved edits alone", () => {
    const view = renderHook(({ loaded }) => useDraft(loaded), { initialProps: { loaded: { v: 1 } } });
    act(() => view.result.current.setDirty(true));
    view.rerender({ loaded: { v: 2 } });
    expect(view.result.current.draft.v).toBe(1);
  });

  it("does nothing when the same data is rendered again", () => {
    const same = { v: 1 };
    const view = renderHook(({ loaded }) => useDraft(loaded), { initialProps: { loaded: same } });
    view.rerender({ loaded: same });
    expect(view.result.current.draft).toBe(same);
  });
});
