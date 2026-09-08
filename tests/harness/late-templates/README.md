# Late-template regression harness

A mod whose own C# registers two entity templates into the game's template maps on a fixed
clock after the title scene (4 s and 25 s), the way another MelonLoader mod does, and whose
KDL depends on those ids in every way a mod can: a patch on one, a clone from one, a clone
chained on that clone, a `ref` nested in a constructed handler, and a patch on the id that
arrives after the scene's poll schedule.

`run.sh` compiles and deploys the mod, waits for a game session, then checks the loader's
log for the hold-and-land lines and reads the live templates through the Studio bridge.
Run it once per loader release, with the dev loader deployed:

```sh
mise run harness:late-templates             # then launch MENACE
mise run harness:late-templates -- --launch # launches it through Steam
```

Every check prints PASS or FAIL; the mod is removed from `Mods/` afterwards (`KEEP=1` keeps it).
