import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { HostNameTip } from "./host-name-tip";

describe("host name tip (#1101)", () => {
  it("tells a container's owner how to name it", () => {
    render(<HostNameTip looksLikeContainerId />);
    expect(screen.getByText(/random id/i).textContent).toContain("hostname:");
    expect(screen.getByText(/random id/i).textContent).toContain("compose file");
  });

  it("says nothing for a real name", () => {
    const { container } = render(<HostNameTip looksLikeContainerId={false} />);
    expect(container.textContent).toBe("");
  });
});
