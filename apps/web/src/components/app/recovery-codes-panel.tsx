import { useState } from "react";
import { Check, Copy } from "lucide-react";
import { Button } from "../ui/button";
import { Checkbox } from "../ui/checkbox";

/**
 * The ten recovery codes, shown once (#1392): a plain note to keep them safe, a way to copy them, and the person's
 * word that they are saved before the screen moves on.
 */
export function RecoveryCodesPanel({
  codes,
  onSaved,
  doneLabel = "Continue"
}: {
  codes: readonly string[];
  onSaved: () => void;
  doneLabel?: string;
}) {
  const [saved, setSaved] = useState(false);
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(codes.join("\n"));
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <div className="space-y-3">
      <p className="text-(length:--type-body-sm) text-muted-foreground">
        Keep these codes somewhere safe, such as a password manager or a printout. Each one works once, and they are not shown again.
      </p>
      <ol
        aria-label="Recovery codes"
        className="grid grid-cols-2 gap-x-6 gap-y-1.5 rounded-xl border border-hairline px-4 py-3 font-mono text-(length:--type-body-sm) text-foreground"
      >
        {codes.map((code) => (
          <li key={code}>{code}</li>
        ))}
      </ol>
      <Button type="button" variant="outline" size="sm" className="gap-2" onClick={() => void copy()}>
        {copied ? <Check className="h-4 w-4" strokeWidth={2} /> : <Copy className="h-4 w-4" strokeWidth={1.75} />}
        {copied ? "Copied" : "Copy codes"}
      </Button>
      <label className="flex cursor-pointer items-center gap-2.5 text-(length:--type-body-sm) text-foreground">
        <Checkbox checked={saved} onCheckedChange={setSaved} />
        I have saved these codes
      </label>
      <Button type="button" className="w-full" disabled={!saved} onClick={onSaved}>
        {doneLabel}
      </Button>
    </div>
  );
}
