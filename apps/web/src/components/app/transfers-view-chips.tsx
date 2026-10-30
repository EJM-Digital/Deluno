import { useSearchParams } from "react-router-dom";
import { FilterChips } from "../ui/filter-chips";
import { HeaderTabsPortal, useHeaderSlot } from "../ui/header-tabs";
import { paramsWithView, TRANSFERS_VIEWS, transfersViewFromParams, type TransfersView } from "../../lib/transfers-view";

/**
 * The one control that narrows In progress to live downloads, hand-offs, or the history (#1426). The view is in the
 * address, so the browser's Back button and a bookmark both return to it. `bare` gives the chips alone, for a
 * toolbar that lays them out itself (the history's filters); otherwise they sit on the header's title line from
 * `lg` up and in a row of their own below it.
 */
export function TransfersViewChips({ bare = false, counts }: { bare?: boolean; counts?: Partial<Record<TransfersView, number>> }) {
  const [searchParams, setSearchParams] = useSearchParams();
  const slot = useHeaderSlot();
  const chips = (
    <FilterChips<TransfersView>
      label="View"
      value={transfersViewFromParams(searchParams)}
      onValueChange={(view) => setSearchParams(paramsWithView(view))}
      options={TRANSFERS_VIEWS.map((view) => ({ ...view, count: counts?.[view.value] }))}
    />
  );
  if (bare) return chips;
  if (slot) return <HeaderTabsPortal slot={slot}>{chips}</HeaderTabsPortal>;
  return <div className="flex min-w-0">{chips}</div>;
}
