using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using SmartphoneAppMessenger;

namespace AppMessengerThreadFix
{
    public class ModEntry : Mod
    {
        internal static int MainThreadId;
        internal static readonly ConcurrentQueue<Action> MainThreadQueue = new();
        internal static IMonitor? SMonitor;

        public override void Entry(IModHelper helper)
        {
            MainThreadId = Environment.CurrentManagedThreadId;
            SMonitor = this.Monitor;

            var harmony = new Harmony(this.ModManifest.UniqueID);

            // Fire-and-forget game APIs: just queue, don't need the result back.
            harmony.Patch(
                original: AccessTools.Method(typeof(MessageManager), nameof(MessageManager.AddMessage)),
                prefix: new HarmonyMethod(typeof(CrossThreadGuard), nameof(CrossThreadGuard.GuardAddMessage))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.addHUDMessage), new[] { typeof(HUDMessage) }),
                prefix: new HarmonyMethod(typeof(CrossThreadGuard), nameof(CrossThreadGuard.GuardAddHudMessage))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(DelayedAction), nameof(DelayedAction.playSoundAfterDelay)),
                prefix: new HarmonyMethod(typeof(CrossThreadGuard), nameof(CrossThreadGuard.GuardPlaySound))
            );

            // The real race: PhoneDialogueRuntime reads/mutates the NPC's live Dialogue object
            // on a background thread while Content Patcher can reload that same NPC's dialogue
            // asset on the main thread. These calls need to BLOCK and run on the main thread,
            // then return the real result back to the caller, or the delivery loop's logic breaks.
            var dialogueType = typeof(Dialogue);

            harmony.Patch(AccessTools.Method(dialogueType, "prepareCurrentDialogueForDisplay"),
                prefix: new HarmonyMethod(typeof(DialogueGuard), nameof(DialogueGuard.GuardPrepare)));

            harmony.Patch(AccessTools.Method(dialogueType, "isDialogueFinished"),
                prefix: new HarmonyMethod(typeof(DialogueGuard), nameof(DialogueGuard.GuardIsFinished)));

            harmony.Patch(AccessTools.Method(dialogueType, "isCurrentDialogueAQuestion"),
                prefix: new HarmonyMethod(typeof(DialogueGuard), nameof(DialogueGuard.GuardIsQuestion)));

            harmony.Patch(AccessTools.Method(dialogueType, "getCurrentDialogue"),
                prefix: new HarmonyMethod(typeof(DialogueGuard), nameof(DialogueGuard.GuardGetCurrentDialogue)));

            harmony.Patch(AccessTools.Method(dialogueType, "getResponseOptions"),
                prefix: new HarmonyMethod(typeof(DialogueGuard), nameof(DialogueGuard.GuardGetResponseOptions)));

            harmony.Patch(AccessTools.Method(dialogueType, "exitCurrentDialogue"),
                prefix: new HarmonyMethod(typeof(DialogueGuard), nameof(DialogueGuard.GuardExitCurrentDialogue)));

            harmony.Patch(AccessTools.Method(dialogueType, "chooseResponse"),
                prefix: new HarmonyMethod(typeof(DialogueGuard), nameof(DialogueGuard.GuardChooseResponse)));

            // Cooldown guard: calling touches NPC friendship/dialogue data, which triggers a
            // heavy Content Patcher reload of every NPC's dialogue across all content packs.
            // If a second call starts while that reload is still running, it reads
            // dialogue objects mid-replacement and crashes. Block new calls for a short
            // window after the last one to let the reload finish first.
            var runtimeType = AccessTools.TypeByName("SmartphoneAppMessenger.PhoneDialogueRuntime");
            if (runtimeType != null)
            {
                harmony.Patch(AccessTools.Method(runtimeType, "FirstDailyText"),
                    prefix: new HarmonyMethod(typeof(CallCooldownGuard), nameof(CallCooldownGuard.GuardFirstDailyText)));
            }
            else
            {
                this.Monitor.Log("Could not find PhoneDialogueRuntime.FirstDailyText to apply call cooldown.", LogLevel.Warn);
            }

            // IMPORTANT: we track AssetReady (fires when an asset actually finishes loading),
            // NOT AssetsInvalidated (fires when CP merely announces an asset is stale). Content
            // Patcher's invalidation announcements and its actual lazy reloads can be many
            // seconds apart — a log showed the "invalidation requested" line at T, then the real
            // ~150-NPC reload cascade (the expensive part) only actually happening at T+33s when
            // something next touched dialogue. A cooldown keyed off the announcement was already
            // "expired" by the time the heavy reload work happened, so it let a second call
            // through right on top of it. Keying off AssetReady instead means the cooldown clock
            // only starts once the actual (expensive) reload work is done.
            helper.Events.Content.AssetReady += (s, e) =>
            {
                if (e.NameWithoutLocale.IsDirectlyUnderPath("Characters/Dialogue"))
                {
                    CallCooldownGuard.RecordInvalidation();
                }
            };

            // Kept as a secondary signal too — harmless if it fires early, since RecordInvalidation
            // just keeps pushing the cooldown window forward; the AssetReady handler above is the
            // one that matters for timing accuracy.
            helper.Events.Content.AssetsInvalidated += (s, e) =>
            {
                foreach (var name in e.NamesWithoutLocale)
                {
                    if (name.IsDirectlyUnderPath("Characters/Dialogue"))
                    {
                        CallCooldownGuard.RecordInvalidation();
                        break;
                    }
                }
            };

            // AppStoreManager.DisposeTextures() (base Smartphone mod) is a no-op left in as a
            // stub — description-image textures and per-mod icon textures are cached forever
            // and never released. This accumulates native texture memory for the whole session
            // and is a real contributor to the OOM aborts seen in crash logs (Mono's lock-free
            // allocator failing to get a heap segment). Postfix the stub with real disposal.
            // AppStoreManager is `internal`, so it's resolved by name via reflection rather
            // than typeof() — no extra project reference needed.
            var appStoreManagerType = AccessTools.TypeByName("Smartphone.AppStoreManager");
            if (appStoreManagerType != null)
            {
                var disposeMethod = AccessTools.Method(appStoreManagerType, "DisposeTextures");
                if (disposeMethod != null)
                {
                    harmony.Patch(disposeMethod,
                        postfix: new HarmonyMethod(typeof(AppStoreTextureFix), nameof(AppStoreTextureFix.RealDispose)));
                    this.Monitor.Log("AppStoreTextureFix: patched AppStoreManager.DisposeTextures.", LogLevel.Debug);
                }
                else
                {
                    this.Monitor.Log("AppStoreTextureFix: DisposeTextures method not found — skipping patch.", LogLevel.Warn);
                }
            }
            else
            {
                this.Monitor.Log("AppStoreTextureFix: Smartphone.AppStoreManager type not found — skipping patch.", LogLevel.Warn);
            }

            // Warm up every NPC's Dialogue asset right at day start, spread one-per-tick.
            // SVE/East Scarp/Eli&Dylan bundle dozens of NPCs into a single comma-separated
            // "Target" in their content.json (one Load entry covers ~40+ NPCs at once) — so
            // whenever CP has to reload ANY one of them (e.g. because the phone call touched
            // that NPC's Dialogue asset), it reloads the whole bundle together. Touching every
            // NPC's Dialogue property ourselves at day start — while nothing else is happening —
            // means the bundle is already fresh by the time the player calls anyone, so calling
            // shouldn't trigger that reload cascade anymore.
            helper.Events.GameLoop.DayStarted += (s, e) =>
            {
                DialogueWarmup.QueueAllNpcs();
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                this.Monitor.Log($"[FATAL/UnhandledException] IsTerminating={e.IsTerminating}\n{e.ExceptionObject}", LogLevel.Alert);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                this.Monitor.Log($"[UnobservedTaskException]\n{e.Exception}", LogLevel.Alert);
                e.SetObserved();
            };

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            this.Monitor.Log("AppMessengerThreadFix: guards + diagnostics active (Dialogue race guard enabled).", LogLevel.Info);
        }

        private void OnUpdateTicked(object? sender, StardewModdingAPI.Events.UpdateTickedEventArgs e)
        {
            while (MainThreadQueue.TryDequeue(out Action? action))
            {
                try { action(); }
                catch (Exception ex) { this.Monitor.Log($"Queued action failed: {ex}", LogLevel.Error); }
            }

            DialogueWarmup.WarmUpOne();
        }
    }

    // Blocking dispatcher: background thread posts work to the main-thread queue and
    // waits (with a timeout) for the real result, so callers get correct values back.
    internal static class MainThreadDispatcher
    {
        private const int TimeoutMs = 3000;

        public static T RunAndWait<T>(Func<T> func)
        {
            if (Environment.CurrentManagedThreadId == ModEntry.MainThreadId)
                return func();

            using var done = new ManualResetEventSlim(false);
            T result = default!;
            Exception? error = null;

            ModEntry.MainThreadQueue.Enqueue(() =>
            {
                try { result = func(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });

            if (!done.Wait(TimeoutMs))
            {
                ModEntry.SMonitor?.Log("[MainThreadDispatcher] Timed out waiting for main thread.", LogLevel.Error);
                return default!;
            }

            if (error != null)
                throw error;

            return result;
        }

        public static void RunAndWait(Action action)
        {
            RunAndWait<object?>(() => { action(); return null; });
        }
    }

    internal static class CrossThreadGuard
    {
        private static bool IsMainThread => Environment.CurrentManagedThreadId == ModEntry.MainThreadId;

        private static void LogOffThreadCall(string apiName)
        {
            string trace = new StackTrace(2, false).ToString();
            ModEntry.SMonitor?.Log(
                $"[CrossThreadGuard] '{apiName}' called from thread {Environment.CurrentManagedThreadId} (main={ModEntry.MainThreadId}). Queuing to main thread.\nCaller trace:\n{trace}",
                LogLevel.Warn);
        }

        internal static bool GuardAddMessage(string npcName, string message, string type)
        {
            if (IsMainThread) return true;
            LogOffThreadCall(nameof(MessageManager.AddMessage));
            ModEntry.MainThreadQueue.Enqueue(() => MessageManager.AddMessage(npcName, message, type));
            return false;
        }

        internal static bool GuardAddHudMessage(HUDMessage message)
        {
            if (IsMainThread) return true;
            LogOffThreadCall(nameof(Game1.addHUDMessage));
            ModEntry.MainThreadQueue.Enqueue(() => Game1.addHUDMessage(message));
            return false;
        }

        internal static bool GuardPlaySound(string soundName, int delay, GameLocation location)
        {
            if (IsMainThread) return true;
            LogOffThreadCall(nameof(DelayedAction.playSoundAfterDelay));
            ModEntry.MainThreadQueue.Enqueue(() => DelayedAction.playSoundAfterDelay(soundName, delay, location));
            return false;
        }
    }

    // Guards for Dialogue methods PhoneDialogueRuntime touches from its background delivery loop.
    // These block-and-wait (not fire-and-forget) because the loop needs the real return value.
    internal static class DialogueGuard
    {
        private static bool IsMainThread => Environment.CurrentManagedThreadId == ModEntry.MainThreadId;

        // Logs EVERY call, even on the main thread, at Trace level — this is purely to verify
        // the patches are actually being hit at all. If you never see these during a call,
        // the patch isn't intercepting the real runtime calls and we need a different target.
        private static void LogHit(string apiName)
        {
            ModEntry.SMonitor?.Log(
                $"[DialogueGuard/HIT] '{apiName}' called on thread {Environment.CurrentManagedThreadId} (main={ModEntry.MainThreadId}).",
                LogLevel.Trace);
        }

        private static void LogOffThreadCall(string apiName)
        {
            string trace = new StackTrace(2, false).ToString();
            ModEntry.SMonitor?.Log(
                $"[DialogueGuard] '{apiName}' called from thread {Environment.CurrentManagedThreadId} (main={ModEntry.MainThreadId}). Redirecting to main thread.\nCaller trace:\n{trace}",
                LogLevel.Warn);
        }

        internal static bool GuardPrepare(Dialogue __instance)
        {
            LogHit("prepareCurrentDialogueForDisplay");
            if (IsMainThread) return true;
            LogOffThreadCall("prepareCurrentDialogueForDisplay");
            MainThreadDispatcher.RunAndWait(() => __instance.prepareCurrentDialogueForDisplay());
            return false;
        }

        internal static bool GuardIsFinished(Dialogue __instance, ref bool __result)
        {
            LogHit("isDialogueFinished");
            if (IsMainThread) return true;
            LogOffThreadCall("isDialogueFinished");
            __result = MainThreadDispatcher.RunAndWait(() => __instance.isDialogueFinished());
            return false;
        }

        internal static bool GuardIsQuestion(Dialogue __instance, ref bool __result)
        {
            LogHit("isCurrentDialogueAQuestion");
            if (IsMainThread) return true;
            LogOffThreadCall("isCurrentDialogueAQuestion");
            __result = MainThreadDispatcher.RunAndWait(() => __instance.isCurrentDialogueAQuestion());
            return false;
        }

        internal static bool GuardGetCurrentDialogue(Dialogue __instance, ref string __result)
        {
            LogHit("getCurrentDialogue");
            if (IsMainThread) return true;
            LogOffThreadCall("getCurrentDialogue");
            __result = MainThreadDispatcher.RunAndWait(() => __instance.getCurrentDialogue());
            return false;
        }

        internal static bool GuardGetResponseOptions(Dialogue __instance, ref Response[] __result)
        {
            LogHit("getResponseOptions");
            if (IsMainThread) return true;
            LogOffThreadCall("getResponseOptions");
            __result = MainThreadDispatcher.RunAndWait(() => __instance.getResponseOptions());
            return false;
        }

        internal static bool GuardExitCurrentDialogue(Dialogue __instance)
        {
            LogHit("exitCurrentDialogue");
            if (IsMainThread) return true;
            LogOffThreadCall("exitCurrentDialogue");
            MainThreadDispatcher.RunAndWait(() => __instance.exitCurrentDialogue());
            return false;
        }

        internal static bool GuardChooseResponse(Dialogue __instance, Response response)
        {
            LogHit("chooseResponse");
            if (IsMainThread) return true;
            LogOffThreadCall("chooseResponse");
            MainThreadDispatcher.RunAndWait(() => __instance.chooseResponse(response));
            return false;
        }
    }

    // Blocks a new call from starting while a mass asset reload (Content Patcher's dialogue
    // reload included) may still be in progress — whether that reload was triggered by our
    // own last call, or happened on its own regular cycle unrelated to calling at all.
    // Also force-runs GC shortly after a reload cascade goes quiet, to reclaim the memory
    // Content Patcher just allocated instead of waiting for the runtime's own GC schedule.
    // On top of that, checks live memory right before allowing a call: if usage is far above
    // the lowest point seen so far this session (i.e. things are unusually elevated right now),
    // it blocks just that one attempt instead of letting the call proceed into a likely crash.
    // This adapts to whatever this session's real baseline turns out to be, rather than using
    // a fixed number — so a naturally RAM-hungry modpack never gets permanently locked out.
    // Warms up every NPC's Dialogue asset (Characters/Dialogue/{name}) one per tick, right at
    // day start. This forces Content Patcher to finish reloading any bundled multi-NPC "Target"
    // entries (e.g. SVE/East Scarp/Eli&Dylan each load 2-40+ NPCs' dialogue in a single comma-
    // separated Target) while nothing else is happening, so the bundle is already fresh by the
    // time the player calls anyone later in the day.
    internal static class DialogueWarmup
    {
        private static readonly Queue<NPC> Pending = new();
        private static bool _queuedToday;

        internal static void QueueAllNpcs()
        {
            try
            {
                Pending.Clear();
                _queuedToday = false;

                List<NPC> all = Utility.getAllCharacters();
                foreach (NPC npc in all)
                    Pending.Enqueue(npc);

                _queuedToday = true;
                ModEntry.SMonitor?.Log($"[DialogueWarmup] Queued {Pending.Count} NPCs for day-start dialogue warm-up.", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[DialogueWarmup] Failed to queue NPCs: {ex.Message}", LogLevel.Warn);
            }
        }

        internal static void WarmUpOne()
        {
            if (!_queuedToday || Pending.Count == 0)
                return;

            NPC npc = Pending.Dequeue();
            try
            {
                // Just touching the getter is enough — it lazily loads Characters/Dialogue/{name}
                // via Game1.content.Load if not already cached. No dialogue is shown, no state changes.
                _ = npc.Dialogue;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[DialogueWarmup] Failed warming up '{npc?.Name}': {ex.Message}", LogLevel.Trace);
            }

            if (Pending.Count == 0)
            {
                _queuedToday = false;
                ModEntry.SMonitor?.Log("[DialogueWarmup] Day-start dialogue warm-up complete.", LogLevel.Debug);
            }
        }
    }

    internal static class CallCooldownGuard
    {
        private const int CooldownMs = 25000; // 25 seconds after the last invalidation event
        private const int GcDebounceMs = 1500; // run GC once reload activity has been quiet this long
        private const long SafetyMarginBytes = 400L * 1024 * 1024; // 400MB above the session's lowest-seen post-GC reading

        private static DateTime _lastInvalidationUtc = DateTime.MinValue;
        private static readonly object Lock = new();
        private static Timer? _gcDebounceTimer;
        private static long _lowestObservedPostGcBytes = long.MaxValue;

        internal static void RecordInvalidation()
        {
            lock (Lock)
            {
                _lastInvalidationUtc = DateTime.UtcNow;
            }

            // (Re)start the debounce timer every time a dialogue asset finishes reloading.
            // As long as the cascade keeps streaming in (dozens of NPCs reloading within the
            // same second), this keeps getting pushed back — the GC only actually runs once
            // GcDebounceMs passes with no further reload activity, i.e. once the cascade is over.
            _gcDebounceTimer?.Dispose();
            _gcDebounceTimer = new Timer(RunPostCascadeGc, null, GcDebounceMs, Timeout.Infinite);
        }

        private static void RunPostCascadeGc(object? state)
        {
            try
            {
                long before = GC.GetTotalMemory(false);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                long after = GC.GetTotalMemory(false);

                RecordPostGcReading(after);

                ModEntry.SMonitor?.Log(
                    $"[CallCooldownGuard] Post-cascade GC: {before / 1024}KB -> {after / 1024}KB (freed {(before - after) / 1024}KB). Session low: {_lowestObservedPostGcBytes / 1024}KB.",
                    LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CallCooldownGuard] Post-cascade GC failed: {ex.Message}", LogLevel.Warn);
            }
        }

        private static void RecordPostGcReading(long bytes)
        {
            lock (Lock)
            {
                if (bytes < _lowestObservedPostGcBytes)
                    _lowestObservedPostGcBytes = bytes;
            }
        }

        // Returns true if it's currently safe to proceed, false if the call should be blocked
        // for this attempt. Tries a GC first if memory looks elevated, since that alone often
        // brings it back down without needing to block anything.
        private static bool IsMemorySafeToCall(string npcName)
        {
            long current = GC.GetTotalMemory(false);
            RecordPostGcReading(current); // a low reading here is just as valid as a post-GC one

            long baseline = _lowestObservedPostGcBytes == long.MaxValue ? current : _lowestObservedPostGcBytes;
            if (current <= baseline + SafetyMarginBytes)
                return true;

            // Elevated — try a real GC pass before giving up on this attempt.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            long afterGc = GC.GetTotalMemory(false);
            RecordPostGcReading(afterGc);

            if (afterGc <= baseline + SafetyMarginBytes)
            {
                ModEntry.SMonitor?.Log(
                    $"[CallCooldownGuard] Memory was elevated for call to '{npcName}' ({current / 1024}KB) but GC brought it back down ({afterGc / 1024}KB) — allowing.",
                    LogLevel.Debug);
                return true;
            }

            ModEntry.SMonitor?.Log(
                $"[CallCooldownGuard] Blocked call to '{npcName}' — memory still elevated after GC ({afterGc / 1024}KB vs session low {baseline / 1024}KB + {SafetyMarginBytes / 1024 / 1024}MB margin).",
                LogLevel.Warn);
            return false;
        }

        internal static bool GuardFirstDailyText(string npcName, string message)
        {
            if (!IsMemorySafeToCall(npcName))
            {
                ModEntry.MainThreadQueue.Enqueue(() =>
                    Game1.addHUDMessage(new HUDMessage("Signal's weak right now — try calling again in a moment.", 3)));
                return false; // skip the original call entirely
            }

            lock (Lock)
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastInvalidationUtc).TotalMilliseconds;
                if (_lastInvalidationUtc != DateTime.MinValue && elapsed < CooldownMs)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CallCooldownGuard] Blocked call to '{npcName}' — only {elapsed:F0}ms since the last asset reload event (cooldown {CooldownMs}ms). " +
                        "This avoids reading NPC dialogue while it may still be mid-reload.",
                        LogLevel.Warn);

                    ModEntry.MainThreadQueue.Enqueue(() =>
                        Game1.addHUDMessage(new HUDMessage("Please wait a moment before calling...", 3)));

                    return false; // skip the original call entirely
                }

                return true; // allow the call, proceed as normal
            }
        }
    }

    // Real disposal logic for Smartphone.AppStoreManager.DisposeTextures(), which upstream
    // was turned into a log-only stub ("disposal is disabled to preserve cache"). Every
    // description-image texture fetched from the web and every per-mod icon texture stays
    // alive in static collections for the rest of the session otherwise. Runs as a Harmony
    // postfix, so the original stub still runs first (harmless) and this does the real work.
    internal static class AppStoreTextureFix
    {
        internal static void RealDispose()
        {
            try
            {
                var appStoreManagerType = AccessTools.TypeByName("Smartphone.AppStoreManager");
                if (appStoreManagerType == null)
                    return;

                int disposedCount = 0;

                // 1) DescriptionImages: Dictionary<string, Texture2D>
                var descProp = AccessTools.Property(appStoreManagerType, "DescriptionImages");
                if (descProp?.GetValue(null) is IDictionary descDict)
                {
                    var keys = new object[descDict.Count];
                    descDict.Keys.CopyTo(keys, 0);
                    foreach (var key in keys)
                    {
                        if (descDict[key] is Texture2D tex && !tex.IsDisposed)
                        {
                            tex.Dispose();
                            disposedCount++;
                        }
                    }
                    descDict.Clear();
                }

                // 2) Per-mod icon textures (public AppStoreMod.IconTexture) on both list properties
                disposedCount += DisposeIconsFrom(appStoreManagerType, "AllMods");
                disposedCount += DisposeIconsFrom(appStoreManagerType, "Mods");

                ModEntry.SMonitor?.Log(
                    $"[AppStoreTextureFix] Disposed {disposedCount} cached textures.",
                    LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[AppStoreTextureFix] Error during real disposal: {ex.Message}", LogLevel.Warn);
            }
        }

        private static int DisposeIconsFrom(Type appStoreManagerType, string propertyName)
        {
            int count = 0;

            var listProp = AccessTools.Property(appStoreManagerType, propertyName);
            if (listProp?.GetValue(null) is IEnumerable mods)
            {
                foreach (var mod in mods)
                {
                    if (mod == null) continue;

                    var iconProp = AccessTools.Property(mod.GetType(), "IconTexture");
                    if (iconProp?.GetValue(mod) is Texture2D tex && !tex.IsDisposed)
                    {
                        tex.Dispose();
                        iconProp.SetValue(mod, null);
                        count++;
                    }
                }
            }

            return count;
        }
    }
}

