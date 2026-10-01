/**
 * What an arrival is called on the shelf (#1098): the library's own name for the title
 * when it has one, else the release read as a title.
 */
import type { ArrivalItem } from "./extras";
import { namedTitle, type TitleLookup } from "./titles";

export function nameOf(item: ArrivalItem, titles: TitleLookup) {
  const named = namedTitle(item.entity, titles, item.release);
  return { title: named?.title ?? item.fallbackTitle, poster: named?.poster, to: named?.to ?? item.to };
}
