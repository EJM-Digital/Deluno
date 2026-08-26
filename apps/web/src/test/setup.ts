import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// `globals: false` means Testing Library never registers its own auto-cleanup,
// so without this every render stays in the document and the next test's query
// finds two of everything. Registered once here rather than in each file.
afterEach(cleanup);
