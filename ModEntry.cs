using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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

            // 1) Known culprit: AddMessage is called from PhoneDialogueRuntime via Task.Run.
            harmony.Patch(
                original: AccessTools.Method(typeof(MessageManager), nameof(MessageManager.AddMessage)),
                prefix: new HarmonyMethod(typeof(CrossThreadGuard), nameof(CrossThreadGuard.GuardAddMessage))
            );

            // 2) Guard the actual game APIs directly, to catch ANY caller (not just the
            //    known one) that might invoke these off the main thread.
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.addHUDMessage), new[] { typeof(HUDMessage) }),
                prefix: new HarmonyMethod(typeof(CrossThreadGuard), nameof(CrossThreadGuard.GuardAddHudMessage))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(DelayedAction), nameof(DelayedAction.playSoundAfterDelay),
                    new[] { typeof(string), typeof(int), typeof(GameLocation) }),
                prefix: new HarmonyMethod(typeof(CrossThreadGuard), nameof(CrossThreadGuard.GuardPlaySound))
            );

            // 3) Process-level safety net in case there's a managed exception we haven't
            //    identified yet (won't catch true native crashes, but will catch anything
            //    that is a .NET exception slipping past normal try/catch).
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
            this.Monitor.Log("AppMessengerThreadFix: guards + diagnostics active.", LogLevel.Info);
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

    internal static class CrossThreadGuard
    {
        private static bool IsMainThread => Environment.CurrentManagedThreadId == ModEntry.MainThreadId;

        private static void LogOffThreadCall(string apiName)
        {
            // Skip frames for this helper + the guard method itself to show the real caller chain.
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

        internal static bool GuardPlaySound(string cueName, int delay, GameLocation location)
        {
            if (IsMainThread) return true;
            LogOffThreadCall(nameof(DelayedAction.playSoundAfterDelay));
            ModEntry.MainThreadQueue.Enqueue(() => DelayedAction.playSoundAfterDelay(cueName, delay, location));
            return false;
        }
    }
}
