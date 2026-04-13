# Jiangyu

Jiangyu is a modkit for MENACE (Unity 6, IL2CPP).

It currently consists of:

- `Jiangyu.Compiler` - a .NET CLI that compiles mod assets into Unity AssetBundles
- `Jiangyu.Loader` - a MelonLoader mod that loads those bundles and applies replacements at runtime

## Requirements

- MENACE
- .NET SDK:
  - .NET 10 SDK for `Jiangyu.Compiler`
  - .NET 6 runtime/tooling for `Jiangyu.Loader`
- MelonLoader `0.7.2`
- Unity Editor `6000.0.63f1` only when building Unity-native assets such as AssetBundles

If a mod only uses non-bundle data paths in the future, Unity should not be required for that workflow. The current model/material replacement pipeline does require Unity because it builds AssetBundles.
