# AppMessenger Thread Fix

Patch mod that guards against calling `Game1.addHUDMessage`, `DelayedAction.playSoundAfterDelay`,
and `MessageManager.AddMessage` from a background thread (which is what `Smartphone-AppMessenger`'s
`PhoneDialogueRuntime` does via `Task.Run` when delivering call/message dialogue). On Android this
kind of cross-thread call tends to crash the whole process with no .NET exception and no SMAPI log
line, which matches the "message shows halfway then the game just closes" symptom.

## What it does
1. Patches `MessageManager.AddMessage`, `Game1.addHUDMessage`, and `DelayedAction.playSoundAfterDelay`.
   If any of them is called off the main thread, the call is queued and replayed on the main thread
   during `UpdateTicked` instead of running immediately.
2. Logs a `[CrossThreadGuard]` warning (with the caller's stack trace) every time this happens, so you
   can see exactly which code path is triggering it.
3. Hooks `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` as a safety net, in
   case there's a *different* unhandled managed exception involved (won't catch a true native crash,
   but will catch anything that's still inside the .NET runtime).

## Build setup (matches your existing CI pattern)
1. Build (or grab) `SmartphoneAppMessenger.dll` from the mod's source and drop it into this project's
   `libs/` folder, same way you already do for the Stardew Android game assemblies.
2. If `HUDMessage`/`Game1`/`DelayedAction` aren't resolving, make sure the same Android game assembly
   set you use for your other mods (SnsAndroidFix, LookupAnythingItemSources, etc.) is also present in
   `libs/`.
3. `dotnet build` as usual — `Pathoschild.Stardew.ModBuildConfig` will package the mod folder
   automatically (rewriting for Android, copying `manifest.json`, etc.), same as your other projects.

## Testing
Enable developer mode logging (already on based on your SMAPI log) and try to reproduce the crash.
- If the game no longer crashes and you see `[CrossThreadGuard]` warnings in the log right before a
  message would have appeared: confirmed root cause, the queue is now catching it.
- If it still crashes with `[CrossThreadGuard]` or `[UnhandledException]`/`[UnobservedTaskException]`
  logged right before the log ends: send the new `SMAPI-latest.txt`, the stack trace will point at the
  next call site to patch.
- If it still crashes with *no* new log lines at all (log just stops like before): it's a true native
  crash unrelated to this specific path, and we'll need to look elsewhere (fonts/rendering, GPU memory).
