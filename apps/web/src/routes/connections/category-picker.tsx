import { Checkbox } from "../../components/ui/checkbox";
import type { MediaScope } from "./presets";
import { categoryIdSelected, customCategoryIds, standardCategoriesForScope, toggleCategoryId } from "./category-catalog";

/**
 * A checkable list of category names, grouped the way Newznab/Torznab
 * itself groups them, replacing the raw "type the numbers" box (#751). Ticks
 * and clears update the same comma-separated Categories value the search
 * request already uses, so no backend contract change is required.
 *
 * A category id the indexer reports (or the field already holds) that isn't
 * one of the known standard ones still shows up, labelled Custom, instead of
 * only ever appearing as a bare number typed into the free-text fallback
 * below this picker.
 */
export function CategoryPicker({ value, onChange, scope }: { value: string; onChange: (next: string) => void; scope: MediaScope }) {
  const groups: { title: string; items: { id: string; name: string }[] }[] = [
    { title: "Movies", items: standardCategoriesForScope(scope).filter((category) => category.group === "Movies") },
    { title: "TV", items: standardCategoriesForScope(scope).filter((category) => category.group === "TV") }
  ].filter((group) => group.items.length > 0);

  const custom = customCategoryIds(value);

  return (
    <div className="flex flex-col gap-3" role="group" aria-label="Categories">
      {groups.map((group) => (
        <div key={group.title}>
          <p className="mb-1 text-[length:var(--type-caption)] font-semibold text-muted-foreground">{group.title}</p>
          <div className="grid grid-cols-2 gap-x-3 gap-y-1.5 sm:grid-cols-3">
            {group.items.map((category) => {
              const checked = categoryIdSelected(value, category.id);
              const id = `category-${category.id}`;
              return (
                <label key={category.id} htmlFor={id} className="flex cursor-pointer items-center gap-1.5">
                  <Checkbox id={id} checked={checked} onCheckedChange={() => onChange(toggleCategoryId(value, category.id))} />
                  <span className="text-[length:var(--type-body-sm)] text-foreground">{category.name}</span>
                  <span className="font-mono text-[length:var(--type-caption)] text-muted-foreground">{category.id}</span>
                </label>
              );
            })}
          </div>
        </div>
      ))}
      {custom.length > 0 ? (
        <div>
          <p className="mb-1 text-[length:var(--type-caption)] font-semibold text-muted-foreground">Custom</p>
          <div className="grid grid-cols-2 gap-x-3 gap-y-1.5 sm:grid-cols-3">
            {custom.map((id) => {
              const checkboxId = `category-custom-${id}`;
              return (
                <label key={id} htmlFor={checkboxId} className="flex cursor-pointer items-center gap-1.5">
                  <Checkbox id={checkboxId} checked onCheckedChange={() => onChange(toggleCategoryId(value, id))} />
                  <span className="font-mono text-[length:var(--type-body-sm)] text-foreground">{id}</span>
                </label>
              );
            })}
          </div>
        </div>
      ) : null}
    </div>
  );
}
