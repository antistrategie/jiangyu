// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";

const rpcCallMock = vi.fn((..._args: unknown[]) => Promise.resolve(null));
vi.mock("@lib/rpc", () => ({
  rpcCall: (...args: unknown[]) => rpcCallMock(...args),
}));

vi.mock("./AgentPanel.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

import { Markdown } from "./Markdown";

afterEach(() => {
  cleanup();
  rpcCallMock.mockClear();
});

describe("Markdown", () => {
  it("renders bold and italic", () => {
    const { container } = render(createElement(Markdown, { text: "**bold** and *italic*" }));
    expect(container.querySelector("strong")?.textContent).toBe("bold");
    expect(container.querySelector("em")?.textContent).toBe("italic");
  });

  it("renders inline code", () => {
    const { container } = render(createElement(Markdown, { text: "use `foo()` to call" }));
    const codes = container.querySelectorAll("code");
    expect(codes.length).toBe(1);
    expect(codes[0]?.textContent).toBe("foo()");
  });

  it("renders fenced code blocks inside <pre><code>", () => {
    const text = "```ts\nconst x = 1;\n```";
    const { container } = render(createElement(Markdown, { text }));
    const pre = container.querySelector("pre");
    expect(pre).not.toBeNull();
    expect(pre?.querySelector("code")?.textContent).toBe("const x = 1;\n");
  });

  it("renders unordered lists", () => {
    const { container } = render(createElement(Markdown, { text: "- one\n- two\n- three" }));
    const items = container.querySelectorAll("ul > li");
    expect(items.length).toBe(3);
    expect(items[0]?.textContent).toBe("one");
  });

  it("renders ordered lists", () => {
    const { container } = render(createElement(Markdown, { text: "1. first\n2. second" }));
    const items = container.querySelectorAll("ol > li");
    expect(items.length).toBe(2);
  });

  it("renders GFM tables", () => {
    const text = ["| h1 | h2 |", "| -- | -- |", "| a  | b  |"].join("\n");
    const { container } = render(createElement(Markdown, { text }));
    expect(container.querySelector("table")).not.toBeNull();
    expect(container.querySelectorAll("th").length).toBe(2);
    expect(container.querySelectorAll("tbody td").length).toBe(2);
  });

  it("renders GFM task lists with checkboxes", () => {
    const text = "- [x] done\n- [ ] todo";
    const { container } = render(createElement(Markdown, { text }));
    const boxes = container.querySelectorAll("input[type=checkbox]");
    expect(boxes.length).toBe(2);
    expect((boxes[0] as HTMLInputElement).checked).toBe(true);
    expect((boxes[1] as HTMLInputElement).checked).toBe(false);
  });

  it("renders GFM strikethrough", () => {
    const { container } = render(createElement(Markdown, { text: "~~gone~~" }));
    expect(container.querySelector("del")?.textContent).toBe("gone");
  });

  it("renders headings", () => {
    const { container } = render(createElement(Markdown, { text: "# H1\n## H2" }));
    expect(container.querySelector("h1")?.textContent).toBe("H1");
    expect(container.querySelector("h2")?.textContent).toBe("H2");
  });

  it("renders blockquotes", () => {
    const { container } = render(createElement(Markdown, { text: "> a quote" }));
    expect(container.querySelector("blockquote")?.textContent.trim()).toBe("a quote");
  });

  it("link click routes through openExternal RPC instead of navigating", () => {
    render(createElement(Markdown, { text: "[docs](https://example.com)" }));
    const link = screen.getByRole("link", { name: "docs" });
    expect(link.getAttribute("href")).toBe("https://example.com");
    fireEvent.click(link);
    expect(rpcCallMock).toHaveBeenCalledWith("openExternal", { url: "https://example.com" });
  });

  it("does not render raw HTML — script tags are escaped to text", () => {
    const { container } = render(
      createElement(Markdown, { text: "before <script>alert(1)</script> after" }),
    );
    // No actual <script> element should be inserted into the DOM. With raw
    // HTML disabled (default), the <script>...</script> string passes
    // through as visible text; we just confirm there's no executable node.
    expect(container.querySelector("script")).toBeNull();
  });
});
