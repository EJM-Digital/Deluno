import { FILTER_MENU_TRIGGER } from "../ui/filter-chips";
import { MenuSelect } from "../ui/menu-select";
import { formatProtocol } from "../../lib/release-candidate-columns";

/**
 * The protocol and indexer pickers in the header of a list of releases (Search results, "Choose a release" on a movie
 * and on a show): compact menus beside the card's title, each shown only when there is more than one thing to pick (#1115).
 */
export function ReleaseFilterMenus({
  protocols,
  protocol,
  onProtocolChange,
  indexers,
  indexer,
  onIndexerChange
}: {
  protocols: readonly string[];
  protocol: string;
  onProtocolChange: (value: string) => void;
  indexers: readonly string[];
  indexer: string;
  onIndexerChange: (value: string) => void;
}) {
  if (protocols.length < 2 && indexers.length < 2) return null;
  return (
    <>
      {protocols.length > 1 ? (
        <MenuSelect
          label="Filter by protocol"
          value={protocol}
          onChange={onProtocolChange}
          options={[{ value: "", label: "Any protocol" }, ...protocols.map((value) => ({ value, label: formatProtocol(value) }))]}
          triggerClassName={FILTER_MENU_TRIGGER}
        />
      ) : null}
      {indexers.length > 1 ? (
        <MenuSelect
          label="Filter by indexer"
          value={indexer}
          onChange={onIndexerChange}
          options={[{ value: "", label: "Any indexer" }, ...indexers.map((name) => ({ value: name, label: name }))]}
          triggerClassName={FILTER_MENU_TRIGGER}
        />
      ) : null}
    </>
  );
}
