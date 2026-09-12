// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement, useState } from "react";
import { render, screen, fireEvent, waitFor, act, cleanup } from "@testing-library/react";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

vi.mock("../TemplateVisualEditor.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

// Mock the virtualiser — jsdom has no layout engine so measurements return 0.
// Provide a minimal implementation that renders all items.
vi.mock("@tanstack/react-virtual", () => ({
  useVirtualizer: (opts: { count: number }) => ({
    getTotalSize: () => opts.count * 28,
    getVirtualItems: () =>
      Array.from({ length: opts.count }, (_, i) => ({
        index: i,
        start: i * 28,
        size: 28,
        key: i,
      })),
  }),
}));

import { SuggestionCombobox } from "./SuggestionCombobox";

// Wrapper that manages controlled value state so the combobox stays in sync.
function Wrapper(props: {
  initialValue: string;
  placeholder: string;
  fetchSuggestions: () => Promise<readonly string[]>;
  onChange?: (v: string) => void;
  onCommit?: (v: string) => void;
}) {
  const [value, setValue] = useState(props.initialValue);
  return createElement(SuggestionCombobox, {
    value,
    placeholder: props.placeholder,
    fetchSuggestions: props.fetchSuggestions,
    onChange: (v: string) => {
      setValue(v);
      props.onChange?.(v);
    },
    // exactOptionalPropertyTypes: only set onCommit when explicitly passed,
    // never assign `undefined` to a typed-as-optional callable prop.
    ...(props.onCommit ? { onCommit: props.onCommit } : {}),
  });
}

beforeEach(() => {
  vi.restoreAllMocks();
});

describe("SuggestionCombobox", () => {
  const items = ["Alpha", "Beta", "Gamma"];
  const fetchSuggestions = vi.fn(() => Promise.resolve(items));

  beforeEach(() => {
    fetchSuggestions.mockClear();
  });

  it("renders with value and placeholder", () => {
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
      }),
    );
    const input = screen.getByPlaceholderText<HTMLInputElement>("Pick one");
    expect(input.value).toBe("");
  });

  it("opens dropdown on focus and calls fetchSuggestions", async () => {
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(fetchSuggestions).toHaveBeenCalledTimes(1));
    // Items should now be visible.
    await waitFor(() => expect(screen.getByText("Alpha")).toBeDefined());
  });

  it("shows loading immediately and ignores a superseded lookup", async () => {
    let resolveOld!: (items: readonly string[]) => void;
    const oldRequest = new Promise<readonly string[]>((resolve) => {
      resolveOld = resolve;
    });
    const { rerender } = render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions: () => oldRequest,
      }),
    );
    fireEvent.focus(screen.getByPlaceholderText("Pick one"));
    expect(screen.getByText("Loading…")).toBeDefined();

    rerender(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions: () => Promise.resolve(["New skill"]),
      }),
    );
    expect(await screen.findByText("New skill")).toBeDefined();
    await act(async () => {
      resolveOld(["Old skill"]);
      await oldRequest;
    });
    expect(screen.queryByText("Old skill")).toBeNull();
    expect(screen.getByText("New skill")).toBeDefined();
    expect(screen.queryByText("Loading…")).toBeNull();
  });

  it("reuses an in-flight lookup across closing and reopening", async () => {
    let resolve!: (items: readonly string[]) => void;
    const request = new Promise<readonly string[]>((done) => {
      resolve = done;
    });
    const fetch = vi.fn(() => request);
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions: fetch,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    fireEvent.focus(input);
    fireEvent.keyDown(input, { key: "Escape" });
    fireEvent.focus(input);
    expect(screen.getByText("Loading…")).toBeDefined();
    expect(fetch).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(input, { key: "Escape" });
    await act(async () => {
      resolve(["Loaded skill"]);
      await request;
    });
    fireEvent.focus(input);
    expect(screen.getByText("Loaded skill")).toBeDefined();
    expect(screen.queryByText("Loading…")).toBeNull();
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("filters items as user types", async () => {
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("Alpha")).toBeDefined());

    act(() => {
      fireEvent.change(input, { target: { value: "bet" } });
    });
    expect(screen.getByText("Beta")).toBeDefined();
    expect(screen.queryByText("Alpha")).toBeNull();
    expect(screen.queryByText("Gamma")).toBeNull();
  });

  it("keeps suggestions and the empty result above an input near the bottom edge", async () => {
    vi.stubGlobal("innerHeight", 768);
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(
      new DOMRect(80, 710, 200, 30),
    );
    render(createElement(Wrapper, { initialValue: "", placeholder: "Pick one", fetchSuggestions }));
    const input = screen.getByPlaceholderText("Pick one");
    fireEvent.focus(input);
    const alpha = await screen.findByText("Alpha");
    const dropdown = alpha.closest<HTMLElement>(".refComboboxDropdown");
    expect(dropdown?.style.top).toBe("auto");
    expect(dropdown?.style.bottom).toBe("58px");
    expect(dropdown?.style.maxHeight).toBe("200px");

    fireEvent.change(input, { target: { value: "no such option" } });
    const empty = screen.getByText("No matches").parentElement;
    expect(empty?.style.top).toBe("auto");
    expect(empty?.style.bottom).toBe("58px");
  });

  it("calls onChange when item clicked", async () => {
    const onChange = vi.fn();
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
        onChange,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("Beta")).toBeDefined());
    act(() => {
      fireEvent.click(screen.getByText("Beta"));
    });
    expect(onChange).toHaveBeenCalledWith("Beta");
  });

  it("closes on Escape", async () => {
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("Alpha")).toBeDefined());
    act(() => {
      fireEvent.keyDown(input, { key: "Escape" });
    });
    expect(screen.queryByText("Alpha")).toBeNull();
  });

  it("selects first filtered item on Enter", async () => {
    const onChange = vi.fn();
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
        onChange,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("Alpha")).toBeDefined());
    act(() => {
      fireEvent.keyDown(input, { key: "Enter" });
    });
    expect(onChange).toHaveBeenCalledWith("Alpha");
  });

  // The onCommit callback distinguishes "user typed" from "user picked" so
  // pick-from-list callers (e.g. HandlerSubtypePicker) can hold typed text
  // as transient state and commit only on real selection. Without this
  // distinction, typing the first letter of a handler subtype committed
  // that letter as the chosen type.

  it("does not call onCommit on every keystroke", async () => {
    const onCommit = vi.fn();
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
        onCommit,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("Alpha")).toBeDefined());
    act(() => {
      fireEvent.change(input, { target: { value: "a" } });
    });
    act(() => {
      fireEvent.change(input, { target: { value: "al" } });
    });
    expect(onCommit).not.toHaveBeenCalled();
  });

  it("calls onCommit when item clicked", async () => {
    const onCommit = vi.fn();
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
        onCommit,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("Beta")).toBeDefined());
    act(() => {
      fireEvent.click(screen.getByText("Beta"));
    });
    expect(onCommit).toHaveBeenCalledWith("Beta");
    expect(onCommit).toHaveBeenCalledTimes(1);
  });

  it("calls onCommit on Enter when filtered list is non-empty", async () => {
    const onCommit = vi.fn();
    render(
      createElement(Wrapper, {
        initialValue: "",
        placeholder: "Pick one",
        fetchSuggestions,
        onCommit,
      }),
    );
    const input = screen.getByPlaceholderText("Pick one");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("Alpha")).toBeDefined());
    act(() => {
      fireEvent.keyDown(input, { key: "Enter" });
    });
    expect(onCommit).toHaveBeenCalledWith("Alpha");
  });
});
