/**
 * #1175: sending a release by hand for a title or episode that is already
 * downloading asks first. The backend refuses with 409 until the person picks
 * one of the three answers; this reads that refusal and asks in plain words.
 */
import { ConfirmDialog } from "../ui/confirm-dialog";

export type DuplicateSendAction = "replace" | "send-as-well";

export interface AlreadyDownloadingPrompt {
  message: string;
}

/** The refusal's question, or null when the response is not the "already downloading" one. */
export async function readAlreadyDownloading(response: Response): Promise<AlreadyDownloadingPrompt | null> {
  if (response.status !== 409) return null;
  try {
    const body = (await response.clone().json()) as { code?: string; message?: string };
    return body.code === "already-downloading" && body.message ? { message: body.message } : null;
  } catch {
    return null;
  }
}

export function AlreadyDownloadingDialog({
  prompt,
  busy,
  onChoose,
  onCancel
}: {
  prompt: AlreadyDownloadingPrompt | null;
  busy: boolean;
  onChoose: (action: DuplicateSendAction) => void;
  onCancel: () => void;
}) {
  return (
    <ConfirmDialog
      open={prompt !== null}
      onOpenChange={(next) => { if (!next) onCancel(); }}
      title="Already downloading"
      description={prompt?.message ?? ""}
      confirmLabel="Replace it"
      confirmVariant="default"
      secondaryLabel="Send as well"
      busy={busy}
      onConfirm={() => onChoose("replace")}
      onSecondary={() => onChoose("send-as-well")}
      extraContent={
        <p className="text-(length:--type-caption) text-muted-foreground">
          Replace removes the first download from its client before sending this one. Send as well keeps both.
        </p>
      }
    />
  );
}
