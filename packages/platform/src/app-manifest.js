export const appManifest = {
  name: "Deluno",
  version: "0.1.0",
  mode: "validation",
  monetization: "none",
  deployment: "self-hosted",
  architecture: {
    shell: "single-app",
    domains: ["movies", "series"],
    boundaryRule: "shared infrastructure only"
  }
};
