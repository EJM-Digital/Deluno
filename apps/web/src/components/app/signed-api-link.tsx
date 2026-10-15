import type { AnchorHTMLAttributes } from "react";
import { toast } from "../shell/toaster";
import { fetchSignedFile } from "../../lib/signed-download";

/**
 * A link to an /api/ file that works signed in (#1331). A plain anchor to an /api/ route is followed without the
 * Authorization header and answers 401, so the click fetches it with the sign-in and saves or opens the result.
 * A refusal shows the server's reason.
 */
export function SignedApiLink({
  path,
  mode,
  fallbackName,
  children,
  ...anchorProps
}: Omit<AnchorHTMLAttributes<HTMLAnchorElement>, "href" | "onClick"> & {
  path: string;
  mode: "save" | "open";
  fallbackName: string;
}) {
  return (
    <a
      href={path}
      {...anchorProps}
      onClick={(event) => {
        event.preventDefault();
        fetchSignedFile(path, mode, fallbackName).catch((error: unknown) => {
          toast.error(error instanceof Error && error.message ? error.message : "That could not be downloaded.");
        });
      }}
    >
      {children}
    </a>
  );
}
