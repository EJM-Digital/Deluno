/**
 * #1101: in Docker the computer's name is the container's random id until the
 * container is given one, and a random id cannot tell two Deluno tabs apart.
 */
export function HostNameTip({ looksLikeContainerId }: { looksLikeContainerId: boolean | undefined }) {
  if (!looksLikeContainerId) return null;
  return (
    <p className="px-1 pt-2 text-(length:--shell-subtle-size) text-muted-foreground">
      This name is the container&apos;s random id. Give the container a name with <code>hostname:</code> in your compose file so you can tell your Deluno tabs apart.
    </p>
  );
}
