# Changelog

Changes relevant to modders building with Jiangyu, and to players running Jiangyu mods where they can see the difference. Entries are scoped Loader, Studio, CLI, MCP or SDK.

## 1.4.0

- (SDK) Added prefix patches that skip the original and set its return value (`info.Skip` + `info.Result`, typed and boxed returns, `null` for nullable returns)
- (SDK) Updated `Tooltip.OnHover` to wait the player's tooltip delay before showing, matching vanilla tooltips
- (SDK) Added weapon bakes that build an Animator from a controller spec, for weapons with moving parts
- (Loader) Fixed index patches on a clone of another mod clone applying against the wrong handler list and warning about missing members at boot
- (Studio) Added a dark theme setting
- (Studio) Added the Open Project picker opening in the folder that holds the most recent project
- (Studio) Fixed the Open Project picker discarding the chosen folder when the dialog stayed open past two minutes
- (Studio) Fixed the UI capture view failing to render captures with omitted fields
- (Studio) Fixed an edit made in the template visual editor overwriting work typed in the source view after switching modes, and reaching the wrong file when the switch was to another tab
- (Studio) Fixed a visual-editor edit dropping the comments at the end of a template file
