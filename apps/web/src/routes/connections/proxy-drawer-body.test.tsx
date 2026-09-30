import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ProxyDrawerBody } from "./proxy-drawer-body";
import { emptyProxyForm } from "./forms";

/** #1076: a proxy is named by Deluno from its type and address, so its drawer has no Name box. */
describe("ProxyDrawerBody", () => {
  it("has no Name box, only the type, address and switches", () => {
    render(
      <ProxyDrawerBody form={emptyProxyForm()} setForm={() => {}} editing={null} errors={{}} clearError={() => {}} busy={null} onRemove={() => {}} />
    );
    expect(screen.queryByLabelText("Name")).toBeNull();
    expect(screen.getByLabelText("Type")).toBeTruthy();
    expect(screen.getByLabelText("Address")).toBeTruthy();
  });
});
