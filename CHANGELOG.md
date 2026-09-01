# Changelog

Changes relevant to modders building with Jiangyu, and to players running Jiangyu mods where they can see the difference. Entries are scoped Loader, Studio, CLI, MCP or SDK.

## 1.4.1

- (SDK) Added `Locale.Format(key, fallback, args)` for a translatable string carrying placeholders, falling back to the mod's own English when a translation breaks one
- (Loader) Fixed a clone showing English for text it inherited from its source, so a cloned skill now translates wherever the vanilla skill it came from does
- (Loader) Fixed text inside an appended list element never reaching the POT, which left appended barks and tooltip entries untranslatable
- (Loader) Fixed text written through an indexed `set` being keyed as if the field were not a collection, and made a negative `index=` a compile error. Retranslate any entry whose key gains an `[N]`
- (Loader) Fixed a mod's cloned conversations going silent partway through a session, leaving the game's own dialogue working until a restart
- (Loader) Fixed replaced textures reverting after a scene change, not applying when their mipmap chain differed from the game's, and flashing the original before a UI replacement landed
- (Loader) Fixed a mod losing its bundled UI and icons when its folder was renamed. A mod's folder can now be named anything and sit anywhere under `Mods/`
- (Loader) Stopped a blocked mod loading its code, which used to run against the bundles and templates the mod was denied
- (Loader) Set the loader to load before other MelonLoader mods

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
