# VGC MTool Bridge

This optional bridge is installed manually in MTool/MCenter by the user.

Boundary:

- Uses `// @AutoLoad` only.
- Calls `getGameCWD()` for context verification.
- Does not call any `@Eval*` engine interface.
- Does not modify game files, NAS source files, MTool `gameLib`, Mods, or third-party scripts.
- Failure must not block VGC cache or launch flow; VGC falls back to the existing Thin Adapter.

Use `probe.js` first. Only use `bridge.js` after the probe confirms MTool can run AutoLoad scripts for the cached game directory.
