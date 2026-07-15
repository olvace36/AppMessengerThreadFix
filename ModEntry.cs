using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
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
}
