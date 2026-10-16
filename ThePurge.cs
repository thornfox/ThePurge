using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("The Purge", "ThornFox", "1.2.1")]
    [Description("Announces and manages a purge event with UI, optional timer, RealPVE integration (unload or PvP API), Notify support and configurable start/end commands.")]
    public class ThePurge : RustPlugin
    {
        #region Fields

        [PluginReference] private Plugin Notify;

        private const string UiName = "PurgeAnnouncerUi";
        private const string InfoUiName = "PurgeInfoPanel";
        private const string PvpZoneId = "ThePurge";
        private const string RealPveName = "RealPVE";
        private const ulong MinSteamId = 76561197960265728UL;

        // Permissions use the plugin prefix (since 1.2.1).
        private const string PermBegin = "thepurge.begin";
        private const string PermEnd = "thepurge.end";
        private const string PermInfo = "thepurge.info";

        // Permission names up to 1.2.0. Existing grants are moved to the new names once.
        private static readonly Dictionary<string, string> LegacyPermissions = new Dictionary<string, string>
        {
            ["purge.begin"] = PermBegin,
            ["purge.end"] = PermEnd,
            ["purge.info"] = PermInfo
        };

        // Hooks that are only needed while a purge is running.
        private static readonly string[] PurgeHooks = { "OnEntitySpawned", "OnPlayerConnected", "OnPlayerRespawned" };

        private enum RealPveMode { Unload, Hooks, Off }

        private ConfigData config;
        private StoredData data;
        private Timer purgeTimer;
        private RealPveMode realPveMode = RealPveMode.Unload;
        private bool legacyActiveFlag;
        private bool pvpApplied;
        private bool markerCreated;
        private readonly HashSet<BaseEntity> pvpEntities = new HashSet<BaseEntity>();

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermBegin, this);
            permission.RegisterPermission(PermEnd, this);
            permission.RegisterPermission(PermInfo, this);

            LoadData();

            // Migration from 1.1.0: the active flag lived in the config and the list of
            // unloaded plugins was kept in memory only. Make sure they get restored on end.
            if (legacyActiveFlag && !data.IsActive)
            {
                data.IsActive = true;
                foreach (var name in GetPluginsToStop())
                {
                    if (FindPlugin(name) == null && !data.StoppedPlugins.Contains(name))
                        data.StoppedPlugins.Add(name);
                }
                SaveData();
                PrintWarning("Active purge from version 1.1.0 detected and taken over.");
            }

            UnsubscribePurgeHooks();
        }

        private void OnServerInitialized(bool initial)
        {
            MigratePermissions();

            if (config.UseNotify && Notify == null)
                PrintWarning("UseNotify is enabled, but the Notify plugin is not loaded.");

            if (!data.IsActive) return;

            if (data.EndUtcTicks > 0 && DateTime.UtcNow.Ticks >= data.EndUtcTicks)
            {
                Puts("The purge timer expired while the plugin was not running. Ending purge.");
                EndPurge(null);
                return;
            }

            Puts("Resuming active purge.");
            ApplyEffects();
            ScheduleEndTimer();
        }

        private void OnNewSave(string filename)
        {
            if (data == null || !data.IsActive) return;

            PrintWarning("Map wipe detected. The active purge is ended.");
            data.IsActive = false;
            data.EndUtcTicks = 0;
            data.InitiatorId = null;
            data.StoppedPlugins.Clear(); // all plugins load normally on server start
            RestoreServerPve();
            SaveData();
        }

        private void Unload()
        {
            purgeTimer?.Destroy();
            purgeTimer = null;

            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, UiName);
                CuiHelper.DestroyUi(player, InfoUiName);
            }

            // On a plugin reload the server must not be left without RealPVE/PvE protection.
            // The purge state stays saved and is resumed when the plugin loads again.
            if (data != null && data.IsActive && !Interface.Oxide.IsShuttingDown)
            {
                PrintWarning("Plugin unloaded during an active purge. Effects are reverted and resumed on next load.");
                RevertEffects();
            }

            if (data != null) SaveData();
        }

        #endregion

        #region Hooks (only subscribed during a purge)

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !data.IsActive) return;

            if (realPveMode == RealPveMode.Hooks) EnterPvp(player);

            timer.Once(2f, () =>
            {
                if (player != null && player.IsConnected && data.IsActive)
                    ShowPurgeAnnouncement(player);
            });
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || !data.IsActive) return;

            // RealPVE removes players from all PvP zones on death.
            if (realPveMode == RealPveMode.Hooks) EnterPvp(player);

            ShowPurgeAnnouncement(player);
        }

        private void OnEntitySpawned(PlayerCorpse corpse)
        {
            if (corpse == null || corpse is NPCPlayerCorpse) return;
            if (corpse.playerSteamID < MinSteamId) return;
            HandlePvpEntity(corpse);
        }

        private void OnEntitySpawned(DroppedItemContainer container)
        {
            if (container == null || container.playerSteamID < MinSteamId) return;
            HandlePvpEntity(container);
        }

        // Always subscribed: keeps the purge consistent when plugins are (re)loaded mid-purge.
        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin == null || plugin == this || data == null || !data.IsActive) return;

            var name = plugin.Name;

            if (GetPluginsToStop().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                NextTick(() =>
                {
                    if (!data.IsActive) return;
                    PrintWarning($"'{name}' was loaded during an active purge and is unloaded again.");
                    StopPlugin(name);
                    if (IsRealPve(name)) ForceServerPveOff();
                });
                return;
            }

            if (realPveMode == RealPveMode.Hooks && IsRealPve(name))
            {
                NextTick(() =>
                {
                    if (!data.IsActive) return;
                    Puts("RealPVE was (re)loaded. Re-applying purge PvP mode.");
                    markerCreated = false;
                    EnterPvpAll();
                    CreateMapMarker();
                });
            }
        }

        #endregion

        #region Commands

        [ChatCommand("pbegin")]
        private void PurgeBeginCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermBegin))
            {
                Reply(player, "NoPermission");
                return;
            }

            int minutes = 0;
            if (args.Length > 0 && (!int.TryParse(args[0], out minutes) || minutes < 0))
            {
                Reply(player, "UsageBegin");
                return;
            }

            Reply(player, BeginPurge(player, minutes) ? "StartedReply" : "AlreadyActive");
        }

        [ChatCommand("pend")]
        private void PurgeEndCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermEnd))
            {
                Reply(player, "NoPermission");
                return;
            }

            Reply(player, EndPurge(player) ? "EndedReply" : "NotActive");
        }

        [ChatCommand("purge")]
        private void PurgeInfoCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermInfo))
            {
                Reply(player, "NoPermission");
                return;
            }

            ShowPurgeInfoPanel(player);
        }

        [ConsoleCommand("purge.begin")]
        private void ConsoleBegin(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermBegin))
            {
                arg.ReplyWith(Msg("NoPermission", player.UserIDString));
                return;
            }

            var minutes = Math.Max(0, arg.GetInt(0, 0));
            arg.ReplyWith(Msg(BeginPurge(player, minutes) ? "StartedReply" : "AlreadyActive", player?.UserIDString));
        }

        [ConsoleCommand("purge.end")]
        private void ConsoleEnd(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermEnd))
            {
                arg.ReplyWith(Msg("NoPermission", player.UserIDString));
                return;
            }

            arg.ReplyWith(Msg(EndPurge(player) ? "EndedReply" : "NotActive", player?.UserIDString));
        }

        #endregion

        #region Core Logic

        private bool BeginPurge(BasePlayer initiator, int minutes)
        {
            if (data.IsActive) return false;

            data.IsActive = true;
            data.InitiatorId = initiator?.UserIDString;
            data.EndUtcTicks = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes).Ticks : 0;
            SaveData();

            ApplyEffects();

            Broadcast("Begun", 1);
            if (minutes > 0) Broadcast("AutoEnd", 0, minutes);

            ExecuteChatCommands(config.ChatCommandsOnStart, initiator);
            ExecuteConsoleCommands(config.ConsoleCommandsOnStart);

            ScheduleEndTimer();
            return true;
        }

        private bool EndPurge(BasePlayer ender)
        {
            if (!data.IsActive) return false;

            var executor = ender ?? FindOnlinePlayer(data.InitiatorId);

            // Mark inactive first, so plugins reloaded below are not unloaded again by OnPluginLoaded.
            data.IsActive = false;
            data.EndUtcTicks = 0;
            data.InitiatorId = null;
            SaveData();

            purgeTimer?.Destroy();
            purgeTimer = null;

            RevertEffects();

            Broadcast("Ended", 2);

            ExecuteChatCommands(config.ChatCommandsOnEnd, executor);
            ExecuteConsoleCommands(config.ConsoleCommandsOnEnd);
            return true;
        }

        private void ApplyEffects()
        {
            SubscribePurgeHooks();

            foreach (var player in BasePlayer.activePlayerList)
                ShowPurgeAnnouncement(player);

            if (realPveMode == RealPveMode.Unload) RememberServerPve();

            foreach (var name in GetPluginsToStop())
                StopPlugin(name);

            switch (realPveMode)
            {
                case RealPveMode.Unload:
                    ForceServerPveOff();
                    break;

                case RealPveMode.Hooks:
                    if (FindPlugin(RealPveName) == null)
                        PrintWarning("RealPveMode is 'Hooks', but RealPVE is not loaded. PvP will be applied as soon as RealPVE loads.");
                    EnterPvpAll();
                    CreateMapMarker();
                    break;
            }
        }

        private void RevertEffects()
        {
            UnsubscribePurgeHooks();

            foreach (var player in BasePlayer.activePlayerList)
                DestroyPurgeAnnouncement(player);

            if (pvpApplied) ExitPvpAll();
            DeleteMapMarker();

            ReloadStoppedPlugins();
            RestoreServerPve();
        }

        private void ScheduleEndTimer()
        {
            purgeTimer?.Destroy();
            purgeTimer = null;

            if (data.EndUtcTicks <= 0) return;

            var remaining = (float)(new DateTime(data.EndUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds;
            if (remaining <= 0f)
            {
                NextTick(() => EndPurge(null));
                return;
            }

            purgeTimer = timer.Once(remaining, () => EndPurge(null));
        }

        private void SubscribePurgeHooks()
        {
            foreach (var hook in PurgeHooks) Subscribe(hook);
        }

        private void UnsubscribePurgeHooks()
        {
            foreach (var hook in PurgeHooks) Unsubscribe(hook);
        }

        #endregion

        #region Plugin Control

        private List<string> GetPluginsToStop()
        {
            var result = new List<string>();

            foreach (var raw in config.PluginsToStop)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var name = raw.Trim();

                if (name.Equals(Name, StringComparison.OrdinalIgnoreCase)) continue;
                // RealPVE is only unloaded in "Unload" mode. In "Hooks" mode it must stay loaded.
                if (IsRealPve(name) && realPveMode != RealPveMode.Unload) continue;
                if (result.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                result.Add(name);
            }

            if (realPveMode == RealPveMode.Unload && !result.Any(IsRealPve))
                result.Add(RealPveName);

            return result;
        }

        private void StopPlugin(string name)
        {
            try
            {
                var plugin = FindPlugin(name);
                if (plugin == null || !plugin.IsLoaded) return;

                var realName = plugin.Name;
                Interface.Oxide.UnloadPlugin(realName);

                if (!data.StoppedPlugins.Contains(realName))
                    data.StoppedPlugins.Add(realName);
                SaveData();

                Puts($"Unloaded '{realName}' for the purge.");
            }
            catch (Exception ex)
            {
                PrintError($"Failed to stop plugin {name}: {ex.Message}");
            }
        }

        private void ReloadStoppedPlugins()
        {
            foreach (var name in data.StoppedPlugins.ToList())
            {
                try
                {
                    if (FindPlugin(name) != null) continue;
                    Interface.Oxide.LoadPlugin(name);
                    Puts($"Reloading '{name}' after the purge.");
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to reload plugin {name}: {ex.Message}");
                }
            }

            data.StoppedPlugins.Clear();
            SaveData();
        }

        private Plugin FindPlugin(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return plugins.Find(name)
                   ?? plugins.GetAll().FirstOrDefault(p => p != null && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsRealPve(string name) =>
            !string.IsNullOrEmpty(name) && name.Trim().Equals(RealPveName, StringComparison.OrdinalIgnoreCase);

        #endregion

        #region RealPVE / server.pve

        // "Unload" mode: RealPVE forces server.pve=true. Without RealPVE that flag would still block PvP,
        // so it is switched off for the purge and restored afterwards.
        private void RememberServerPve()
        {
            if (!config.DisableServerPveDuringPurge || data.HasPreviousPve) return;

            data.PreviousPve = ConVar.Server.pve;
            data.HasPreviousPve = true;
            SaveData();
        }

        private void ForceServerPveOff()
        {
            if (!config.DisableServerPveDuringPurge || realPveMode != RealPveMode.Unload) return;

            SetServerPve(false);
            // Second pass in case an unloading plugin resets the flag in its own Unload.
            NextTick(() =>
            {
                if (data.IsActive && realPveMode == RealPveMode.Unload && ConVar.Server.pve)
                    SetServerPve(false);
            });
        }

        private void RestoreServerPve()
        {
            if (!data.HasPreviousPve) return;

            SetServerPve(data.PreviousPve);
            data.HasPreviousPve = false;
            SaveData();
        }

        private void SetServerPve(bool value)
        {
            ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "server.pve", value ? "true" : "false");
            Puts($"server.pve set to {value.ToString().ToLower()}.");
        }

        // "Hooks" mode: universal PvP hooks documented by RealPVE (also understood by other PvE plugins).
        private void EnterPvp(BasePlayer player)
        {
            if (player == null || player.IsDestroyed || player.IsNpc) return;
            Interface.CallHook("OnPlayerEnterPVP", player, PvpZoneId);
            pvpApplied = true;
        }

        private void EnterPvpAll()
        {
            foreach (var player in BasePlayer.activePlayerList.ToList())
                EnterPvp(player);

            if (config.HooksIncludeSleepers)
            {
                foreach (var player in BasePlayer.sleepingPlayerList.ToList())
                    EnterPvp(player);
            }
        }

        private void ExitPvpAll()
        {
            foreach (var player in BasePlayer.allPlayerList.ToList())
            {
                if (player == null || player.IsDestroyed || player.IsNpc) continue;
                Interface.CallHook("OnPlayerExitPVP", player, PvpZoneId, config.HooksPvpDelayOnEnd);
            }

            foreach (var entity in pvpEntities)
            {
                if (entity == null || entity.IsDestroyed) continue;
                Interface.CallHook("OnEntityExitPVP", entity, PvpZoneId, 0f);
            }

            pvpEntities.Clear();
            pvpApplied = false;
        }

        private void HandlePvpEntity(BaseEntity entity)
        {
            if (!data.IsActive || realPveMode != RealPveMode.Hooks || !config.HooksMakeCorpsesLootable) return;

            NextTick(() =>
            {
                if (entity == null || entity.IsDestroyed || !data.IsActive) return;
                Interface.CallHook("OnEntityEnterPVP", entity, PvpZoneId);
                pvpEntities.Add(entity);
            });
        }

        private void CreateMapMarker()
        {
            if (realPveMode != RealPveMode.Hooks || !config.HooksCreateMapMarker || markerCreated) return;

            Interface.CallHook("CreatePVPMapMarker", PvpZoneId, Vector3.zero, World.Size / 2f, config.HooksMapMarkerName);
            markerCreated = true;
        }

        private void DeleteMapMarker()
        {
            if (!markerCreated) return;

            Interface.CallHook("DeletePVPMapMarker", PvpZoneId);
            markerCreated = false;
        }

        #endregion

        #region UI

        private void ShowPurgeAnnouncement(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            DestroyPurgeAnnouncement(player);

            var container = new CuiElementContainer();
            container.Add(new CuiLabel
            {
                Text = { Text = Msg("Banner", player.UserIDString), FontSize = 22, Align = TextAnchor.MiddleCenter, Color = "1 0 0 1" },
                RectTransform = { AnchorMin = "0.4 0.9", AnchorMax = "0.6 0.95" }
            }, "Overlay", UiName);

            CuiHelper.AddUi(player, container);
        }

        private void DestroyPurgeAnnouncement(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiName);
        }

        private void ShowPurgeInfoPanel(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, InfoUiName);

            var id = player.UserIDString;
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.85" },
                RectTransform = { AnchorMin = "0.28 0.25", AnchorMax = "0.72 0.75" },
                CursorEnabled = true
            }, "Overlay", InfoUiName);

            container.Add(new CuiLabel
            {
                Text = { Text = Msg("InfoTitle", id), FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0.88", AnchorMax = "1 0.98" }
            }, InfoUiName);

            container.Add(new CuiButton
            {
                Button = { Close = InfoUiName, Color = "0.8 0 0 0.9" },
                Text = { Text = "X", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.92 0.90", AnchorMax = "0.98 0.98" }
            }, InfoUiName);

            container.Add(new CuiLabel
            {
                Text = { Text = GetStatusText(id), FontSize = 15, Align = TextAnchor.MiddleLeft, Color = data.IsActive ? "1 0.3 0.3 1" : "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.04 0.80", AnchorMax = "0.96 0.87" }
            }, InfoUiName);

            var lines = config.PurgeInformation.Where(l => l != null).ToList();
            var infoText = lines.Count > 0 ? string.Join("\n", lines) : Msg("InfoEmpty", id);

            container.Add(new CuiLabel
            {
                Text = { Text = infoText, FontSize = 16, Align = TextAnchor.UpperLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.04 0.06", AnchorMax = "0.96 0.78" }
            }, InfoUiName);

            CuiHelper.AddUi(player, container);
        }

        private string GetStatusText(string userId)
        {
            if (!data.IsActive) return Msg("InfoStatusInactive", userId);
            if (data.EndUtcTicks <= 0) return Msg("InfoStatusActive", userId);

            var minutes = Math.Max(1, (int)Math.Ceiling((new DateTime(data.EndUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalMinutes));
            return Msg("InfoStatusActiveTimed", userId, minutes);
        }

        #endregion

        #region Output Helpers

        // Direct replies to the command caller are always shown, independent of UseChat.
        private void Reply(BasePlayer player, string key, params object[] args)
        {
            if (player == null)
            {
                Puts(Msg(key, null, args));
                return;
            }

            player.ChatMessage(Msg(key, player.UserIDString, args));
        }

        private void Broadcast(string key, int notifyType, params object[] args)
        {
            Puts(Msg(key, null, args));

            foreach (var player in BasePlayer.activePlayerList)
            {
                var text = Msg(key, player.UserIDString, args);
                if (config.UseChat) player.ChatMessage(text);
                SendNotification(player, notifyType, text);
            }
        }

        private void SendNotification(BasePlayer player, int type, string message)
        {
            if (!config.UseNotify || Notify == null || player == null) return;

            try { Notify.Call("SendNotify", player, type, message); }
            catch (Exception ex) { PrintError($"Notify call failed: {ex.Message}"); }
        }

        private static BasePlayer FindOnlinePlayer(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return null;
            return BasePlayer.activePlayerList.FirstOrDefault(p => p != null && p.UserIDString == userId);
        }

        #endregion

        #region Command Execution

        // Chat commands are executed as the player who started/ended the purge (same rights as that player).
        private void ExecuteChatCommands(List<string> commands, BasePlayer executor)
        {
            if (commands == null || commands.Count == 0) return;

            if (executor == null || !executor.IsConnected)
            {
                PrintWarning("Chat commands skipped: no online player to execute them (purge started/ended by console or timer). Use console commands for automated runs.");
                return;
            }

            foreach (var raw in commands)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var command = "/" + raw.Trim().TrimStart('/');
                executor.SendConsoleCommand("chat.say", command);
            }
        }

        private void ExecuteConsoleCommands(List<string> commands)
        {
            if (commands == null) return;

            foreach (var raw in commands)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try { ConsoleSystem.Run(ConsoleSystem.Option.Server, raw.Trim()); }
                catch (Exception ex) { PrintError($"Console command '{raw}' failed: {ex.Message}"); }
            }
        }

        #endregion

        #region Config

        private class ConfigData
        {
            public string Version { get; set; }

            // Legacy (1.1.0). Only read for migration, removed from the file afterwards.
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool IsPurgeActive { get; set; }

            public bool UseNotify { get; set; } = false;
            public bool UseChat { get; set; } = true;
            public List<string> PurgeInformation { get; set; } = new List<string>();
            public List<string> PluginsToStop { get; set; } = new List<string>();

            // Unload = RealPVE is unloaded (all rules off, incl. raiding)
            // Hooks  = RealPVE stays loaded, players are switched to PvP via the RealPVE API
            // Off    = RealPVE is not touched
            public string RealPveMode { get; set; } = "Unload";
            public bool DisableServerPveDuringPurge { get; set; } = true;
            public bool HooksIncludeSleepers { get; set; } = true;
            public bool HooksMakeCorpsesLootable { get; set; } = true;
            public float HooksPvpDelayOnEnd { get; set; } = 5f;
            public bool HooksCreateMapMarker { get; set; } = false;
            public string HooksMapMarkerName { get; set; } = "PURGE";

            public List<string> ChatCommandsOnStart { get; set; } = new List<string>();
            public List<string> ConsoleCommandsOnStart { get; set; } = new List<string>();
            public List<string> ChatCommandsOnEnd { get; set; } = new List<string>();
            public List<string> ConsoleCommandsOnEnd { get; set; } = new List<string>();

            public void Normalize()
            {
                PurgeInformation = PurgeInformation ?? new List<string>();
                PluginsToStop = PluginsToStop ?? new List<string>();
                ChatCommandsOnStart = ChatCommandsOnStart ?? new List<string>();
                ConsoleCommandsOnStart = ConsoleCommandsOnStart ?? new List<string>();
                ChatCommandsOnEnd = ChatCommandsOnEnd ?? new List<string>();
                ConsoleCommandsOnEnd = ConsoleCommandsOnEnd ?? new List<string>();
                if (string.IsNullOrWhiteSpace(RealPveMode)) RealPveMode = "Unload";
                if (string.IsNullOrWhiteSpace(HooksMapMarkerName)) HooksMapMarkerName = "PURGE";
                if (HooksPvpDelayOnEnd < 0f) HooksPvpDelayOnEnd = 0f;
            }
        }

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData
            {
                Version = Version.ToString(),
                PurgeInformation = new List<string>
                {
                    "Welcome to the Purge event!",
                    "All rules are temporarily lifted during the purge.",
                    "Players can fight, loot, and raid freely.",
                    "Have fun, but play fair!"
                },
                PluginsToStop = new List<string> { "RealPVE", "SleeperGuard" }
            };
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<ConfigData>();
                if (config == null) throw new JsonException("Config is empty");
            }
            catch (Exception ex)
            {
                // Do not overwrite a broken file: the admin may still want to fix it by hand.
                PrintError($"Config could not be loaded ({ex.Message}). Default values are used for this session; the file was not changed.");
                LoadDefaultConfig();
                ParseMode();
                return;
            }

            config.Normalize();
            MigrateConfig();
            ParseMode();
            SaveConfig();
        }

        private void MigrateConfig()
        {
            if (config.IsPurgeActive)
            {
                legacyActiveFlag = true;
                config.IsPurgeActive = false;
            }

            var current = Version.ToString();
            if (config.Version == current) return;

            // 1.1.0 only printed "chat commands" as plain text. Its default "say ..." line is a console command.
            const string oldDefault = "say Purge is over";
            if (config.ChatCommandsOnEnd.RemoveAll(c => c != null && c.Trim().Equals(oldDefault, StringComparison.OrdinalIgnoreCase)) > 0
                && !config.ConsoleCommandsOnEnd.Contains(oldDefault))
            {
                config.ConsoleCommandsOnEnd.Add(oldDefault);
            }

            PrintWarning($"Config migrated from {config.Version ?? "1.1.0"} to {current}. Please review 'RealPveMode' and the chat command lists.");
            config.Version = current;
        }

        private void ParseMode()
        {
            if (!Enum.TryParse(config.RealPveMode, true, out realPveMode) || !Enum.IsDefined(typeof(RealPveMode), realPveMode))
            {
                PrintWarning($"Unknown RealPveMode '{config.RealPveMode}'. Allowed: Unload, Hooks, Off. Using 'Unload'.");
                realPveMode = RealPveMode.Unload;
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region Permission Migration

        // Moves grants of the old purge.* permissions (up to 1.2.0) to thepurge.* and revokes the old ones.
        // Runs once; the result is stored in the data file.
        private void MigratePermissions()
        {
            if (data.PermissionsMigrated) return;

            int groups = 0, users = 0;
            foreach (var pair in LegacyPermissions)
            {
                try
                {
                    foreach (var group in permission.GetPermissionGroups(pair.Key) ?? new string[0])
                    {
                        if (string.IsNullOrEmpty(group)) continue;
                        permission.GrantGroupPermission(group, pair.Value, this);
                        permission.RevokeGroupPermission(group, pair.Key);
                        groups++;
                    }

                    // Entries are returned as "steamId(name)".
                    foreach (var entry in permission.GetPermissionUsers(pair.Key) ?? new string[0])
                    {
                        if (string.IsNullOrEmpty(entry)) continue;
                        var userId = entry.Split('(')[0].Trim();
                        if (string.IsNullOrEmpty(userId)) continue;
                        permission.GrantUserPermission(userId, pair.Value, this);
                        permission.RevokeUserPermission(userId, pair.Key);
                        users++;
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"Failed to migrate permission {pair.Key}: {ex.Message}");
                }
            }

            if (groups + users > 0)
                PrintWarning($"Permissions renamed to thepurge.*: moved {groups} group grant(s) and {users} user grant(s) from purge.*.");

            data.PermissionsMigrated = true;
            SaveData();
        }

        #endregion

        #region Data

        private class StoredData
        {
            public bool PermissionsMigrated;
            public bool IsActive;
            public long EndUtcTicks;
            public string InitiatorId;
            public List<string> StoppedPlugins = new List<string>();
            public bool HasPreviousPve;
            public bool PreviousPve;
        }

        private void LoadData()
        {
            try { data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name); }
            catch (Exception ex) { PrintError($"Data file could not be loaded: {ex.Message}"); }

            if (data == null) data = new StoredData();
            if (data.StoppedPlugins == null) data.StoppedPlugins = new List<string>();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, data);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You do not have permission to use this command.",
                ["UsageBegin"] = "Usage: /pbegin [minutes] (without minutes the purge runs until /pend)",
                ["AlreadyActive"] = "The purge is already active.",
                ["NotActive"] = "There is no active purge.",
                ["StartedReply"] = "Purge successfully started.",
                ["EndedReply"] = "Purge successfully ended.",
                ["Begun"] = "The Purge has begun.",
                ["Ended"] = "The Purge has ended.",
                ["AutoEnd"] = "The Purge will automatically end in {0} minute(s).",
                ["Banner"] = "PURGE ACTIVE",
                ["InfoTitle"] = "Purge Information",
                ["InfoStatusActive"] = "Status: ACTIVE",
                ["InfoStatusActiveTimed"] = "Status: ACTIVE - ends in {0} minute(s)",
                ["InfoStatusInactive"] = "Status: not active",
                ["InfoEmpty"] = "No information available."
            }, this);
        }

        private string Msg(string key, string userId = null, params object[] args)
        {
            var text = lang.GetMessage(key, this, userId);
            return args != null && args.Length > 0 ? string.Format(text, args) : text;
        }

        #endregion
    }
}
