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
