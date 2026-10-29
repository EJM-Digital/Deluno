/**
 * Account -- the way back into Deluno if the username or password is forgotten (#1392).
 *
 *   PageToolbar (System settings tabs)
 *   ListCard  recovery codes   (how many are left, make a new set)
 */
import { ListCard } from "../components/ui/list-card";
import { PageToolbar } from "../components/ui/page-toolbar";
import { RecoveryCodesManager } from "../components/app/recovery-codes-manager";
import { systemSettingsNavItems } from "../components/app/settings-shell";

export function SettingsAccountPage() {
  return (
    <div className="flex flex-col gap-(--page-gap)">
      <PageToolbar tabs={systemSettingsNavItems} />

      <ListCard
        title="Recovery codes"
        count="The way back in if you forget your username or password"
        info="Each code works once. Using one shows your username and lets you set a new password, which signs every device out. Making a new set stops the old codes working. Deluno keeps only a scrambled copy of each code, so it cannot show them again."
      >
        <div className="p-(--card-pad-x)">
          <RecoveryCodesManager />
        </div>
      </ListCard>
    </div>
  );
}
