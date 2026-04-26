# Jiangyu

Jiangyu is a modkit for MENACE.

## Requirements

- MENACE
- .NET SDK:
  - .NET 10 SDK for `Jiangyu.Cli`
  - .NET 6 runtime/tooling for `Jiangyu.Loader`
- MelonLoader `0.7.2`
- Unity Editor `6000.0.63f1` (when building Unity-native assets such as AssetBundles)

See the [Jiangyu docs site](https://antistrategie.github.io/jiangyu/) for the current modder-facing workflow and path conventions. The site sources live under `site/` (VitePress) and ship via the `pages` GitHub Actions workflow.

## Jiangyu Development

`Directory.Build.props` assumes a standard MENACE install path for local Jiangyu development. If your game is installed elsewhere, create a gitignored `local.props` file in the repo root with your override:

```xml
<Project>
  <PropertyGroup>
    <GameDir>/path/to/your/Menace</GameDir>
  </PropertyGroup>
</Project>
```

`local.props` is only for building Jiangyu itself. Runtime commands use Jiangyu's global config for `game`, `unityEditor`, and `cache`.

This repo uses `mise` as a task runner for common development commands. Use `mise tasks` to see the available tasks in `mise.toml`.
