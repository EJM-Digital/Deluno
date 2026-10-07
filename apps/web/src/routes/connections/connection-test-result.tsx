import { Chip } from "../../components/ui/chip";
import { DrawerSection } from "../../components/ui/drawer";
import { healthChip } from "./format";
import { MessageWithPreferencesLink } from "./preferences-link";

export type ConnectionTestOutcome = { healthStatus: string; message: string };

/**
 * What Test found for the settings in the drawer (#626), shown in the drawer
 * beside the form it tested, not only as a toast.
 */
export function ConnectionTestResult({ result }: { result: ConnectionTestOutcome }) {
  const chip = healthChip({ isEnabled: true, healthStatus: result.healthStatus });
  return (
    <DrawerSection title="Test">
      <p role="status" aria-live="polite" className="flex flex-wrap items-center gap-2 text-(length:--type-body-sm)">
        <Chip tone={chip.tone}>{chip.label}</Chip>
        <span className="text-foreground"><MessageWithPreferencesLink text={result.message} /></span>
      </p>
    </DrawerSection>
  );
}
