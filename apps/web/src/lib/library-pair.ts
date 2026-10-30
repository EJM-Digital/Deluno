/**
 * "Both" when adding a library (#1411) makes a Movies library and a TV library under one folder. This is the layout
 * the server makes (`PairMember` in the Libraries endpoints), worked out here so the person sees it before saving.
 */
export interface LibraryPairPart {
  mediaType: "movies" | "tv";
  name: string;
  folder: string;
}

export function libraryPairPlan(name: string, rootPath: string): LibraryPairPart[] {
  const root = rootPath.trim().replace(/[\\/]+$/, "");
  const separator = root.includes("\\") && !root.includes("/") ? "\\" : "/";
  const base = name.trim();
  return [
    { mediaType: "movies", name: base ? `${base} Movies` : "", folder: root ? `${root}${separator}Movies` : "" },
    { mediaType: "tv", name: base ? `${base} TV` : "", folder: root ? `${root}${separator}TV` : "" }
  ];
}
