export interface DatabaseDescriptor {
  key: string;
  fileName: string;
  purpose: string;
}

export interface ModuleDescriptor {
  name: string;
  purpose: string;
}

export interface SystemManifest {
  app: string;
  storageRoot: string;
  modules: ModuleDescriptor[];
  databases: DatabaseDescriptor[];
}

export interface MovieListItem {
  id: string;
  title: string;
  releaseYear: number | null;
  imdbId: string | null;
  monitored: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface SeriesListItem {
  id: string;
  title: string;
  startYear: number | null;
  imdbId: string | null;
  monitored: boolean;
  createdUtc: string;
  updatedUtc: string;
}

export interface ValidationProblem {
  title?: string;
  errors?: Record<string, string[]>;
}

export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, init);
  if (!response.ok) {
    throw new Error(`Request failed for ${path} with status ${response.status}.`);
  }

  return (await response.json()) as T;
}

export async function readValidationProblem(
  response: Response
): Promise<ValidationProblem | null> {
  try {
    return (await response.json()) as ValidationProblem;
  } catch {
    return null;
  }
}
