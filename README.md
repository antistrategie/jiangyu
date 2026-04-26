# Jiangyu

General-purpose modkit for MENACE (Unity 6, IL2CPP). Mods are files: drop replacement assets by name, patch DataTemplates with KDL if you need to, compile in Studio.

## For modders

See the [Jiangyu docs site](https://antistrategie.github.io/jiangyu/) for the full workflow, path conventions, and KDL reference.

## Requirements

- MENACE
- .NET 10 SDK (CLI), .NET 6 runtime (Loader)
- MelonLoader 0.7.2
- Unity Editor 6000.0.72f1 (when building AssetBundles)
- bun (Studio UI and docs site)

## Development

This repo uses [mise](https://mise.jdx.dev/) as a task runner:

```bash
mise run build        # Build the full solution
mise run test         # Run all tests
mise run studio       # Build and run Jiangyu Studio
mise run studio:dev   # Run Studio with Vite HMR
mise run format       # Roslyn + Prettier formatting
```

If your MENACE install path differs from the default in `Directory.Build.props`, create a gitignored `local.props`:

```xml
<Project>
  <PropertyGroup>
    <GameDir>/path/to/your/Menace</GameDir>
  </PropertyGroup>
</Project>
```
