# Changelog

Changes relevant to modders building with Jiangyu, and to players running Jiangyu mods where they can see the difference.

## 1.4.0

- Added prefix patches that skip the original and set its return value (`info.Skip` + `info.Result`, typed and boxed returns, `null` for nullable returns)
- Added weapon bakes that build an Animator from a controller spec, for weapons with moving parts
- Added a dark theme to Studio
- Updated `Tooltip.OnHover` to wait the player's tooltip delay before showing, matching vanilla tooltips
- Fixed index patches on a clone of another mod clone applying against the wrong handler list and warning about missing members at boot
- Fixed a unit's exclusive item showing up in other restricted units' equipment dropdowns
- Fixed Studio's UI capture view failing to render captures with omitted fields
