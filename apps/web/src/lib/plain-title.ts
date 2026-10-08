import * as React from "react";

/**
 * The text a React node says, for a tooltip on a value the screen cuts short (#1168). Strings, numbers, arrays and
 * the children of elements and fragments count; a component that renders its own text cannot be read from here and
 * says nothing. Empty when there is nothing to say.
 */
export function textOf(node: React.ReactNode): string {
  if (typeof node === "string") return node;
  if (typeof node === "number") return String(node);
  if (Array.isArray(node)) return node.map(textOf).join("");
  if (React.isValidElement(node)) return textOf((node.props as { children?: React.ReactNode }).children);
  return "";
}

/** `textOf` as a `title` value: undefined when the node says nothing. */
export function plainTitle(node: React.ReactNode): string | undefined {
  return textOf(node).replace(/\s+/g, " ").trim() || undefined;
}
