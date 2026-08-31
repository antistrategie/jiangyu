# Changelog

Changes relevant to modders building with Jiangyu, and to players running Jiangyu mods where they can see the difference. Entries are scoped Loader, Studio, CLI, MCP or SDK.

## 1.4.1

- (SDK) Added `Locale.Format(key, fallback, args)` for a translatable string carrying placeholders. Placeholders are part of what a translator edits, so a translation can drop one, renumber it or leave a brace unclosed; this falls back to the mod's own English rather than throwing part-way through building a screen, and reaches the POT exactly as `Locale.Text` does
- (Loader) Fixed four ways localised text went missing. A clone showed English for text it inherited from its source and never overrode, leaving a cloned weapon skill untranslated while the vanilla skill it came from translated normally; it now reads as its source does in every language, with nothing for a translator to fill in. Text nested inside an appended list element never reached the POT at all, so barks on a leader's emotional-state responses and entries appended to a tooltip config could not be translated. Text written through an indexed `set "Field" index=N type="..."` was keyed as if the field were not a collection, which no translation could apply against, so retranslate any entry whose key gains an `[N]`. And a negative `index=` is now rejected at compile time rather than compiling and failing when the patch applied
- (Loader) Fixed a replaced texture reverting to the original after a scene change, when the game texture it was written into was one the engine could unload and re-read from disk
- (Loader) Fixed a replaced texture silently not applying when its mipmap chain differed from the game texture's, which left the original in place with only a warning
- (Loader) Fixed a replaced UI texture landing a few frames after the screen that draws it had already painted, so the original flashed on screen first

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
