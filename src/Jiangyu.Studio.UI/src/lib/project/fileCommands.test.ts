import { describe, expect, it, vi } from "vitest";
import { fileTargetCommands } from "@lib/project/fileCommands.ts";

describe("fileTargetCommands", () => {
  const path = "/home/justin/dev/project/src/App.tsx";
  const projectPath = "/home/justin/dev/project";

  it("returns close/copyPath/copyRelativePath/reveal in that order", () => {
    const commands = fileTargetCommands(path, projectPath, () => {});
    expect(commands.map((c) => c.id)).toEqual(["close", "copyPath", "copyRelativePath", "reveal"]);
  });

  it("tags close with Ctrl+W shortcut and a CJK label", () => {
    const close = fileTargetCommands(path, projectPath, () => {}).find((c) => c.id === "close")!;
    expect(close.shortcut).toBe("Ctrl+W");
    expect(close.cn).toBe("关闭");
  });

  it("sets copyRelativePath desc to the project-relative path", () => {
    const cmd = fileTargetCommands(path, projectPath, () => {}).find(
      (c) => c.id === "copyRelativePath",
    )!;
    expect(cmd.desc).toBe("src/App.tsx");
  });

  it("close runs onCloseFiles with just the target path", () => {
    const onClose = vi.fn<(paths: string[]) => void>();
    const close = fileTargetCommands(path, projectPath, onClose).find((c) => c.id === "close")!;
    close.run();
    expect(onClose).toHaveBeenCalledWith([path]);
  });

  it("copyPath writes the absolute path to clipboard", () => {
    const writeText = vi.fn<(s: string) => Promise<void>>().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const cmd = fileTargetCommands(path, projectPath, () => {}).find((c) => c.id === "copyPath")!;
    cmd.run();
    expect(writeText).toHaveBeenCalledWith(path);
    vi.unstubAllGlobals();
  });

  it("copyRelativePath writes the relative path to clipboard", () => {
    const writeText = vi.fn<(s: string) => Promise<void>>().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const cmd = fileTargetCommands(path, projectPath, () => {}).find(
      (c) => c.id === "copyRelativePath",
    )!;
    cmd.run();
    expect(writeText).toHaveBeenCalledWith("src/App.tsx");
    vi.unstubAllGlobals();
  });

  it("copyRelativePath falls back to the absolute path when file is outside project", () => {
    const writeText = vi.fn<(s: string) => Promise<void>>().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const outside = "/etc/hosts";
    const cmd = fileTargetCommands(outside, projectPath, () => {}).find(
      (c) => c.id === "copyRelativePath",
    )!;
    cmd.run();
    expect(writeText).toHaveBeenCalledWith(outside);
    vi.unstubAllGlobals();
  });
});
