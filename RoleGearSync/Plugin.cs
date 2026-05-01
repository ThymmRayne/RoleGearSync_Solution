using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Bindings.ImGui;
using System.Collections.Generic;

namespace RoleGearSync
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "RoleGearSync";
        private const string CommandName = "/syncgear";

        // Dalamud Services
        private IDalamudPluginInterface PluginInterface { get; init; }
        private ICommandManager CommandManager { get; init; }
        private IChatGui ChatGui { get; init; }
        private IFramework Framework { get; init; }
        private IClientState ClientState { get; init; }
        private IObjectTable ObjectTable { get; init; }

        // UI State
        private bool isUiVisible = false;

        // State Machine Variablen für Ansatz A
        private enum SyncState
        {
            Idle,
            SwitchingJob,
            WaitingForSwitch,
            SetupOptimization,
            WaitingForCalculation,
            EquippingGear,
            WaitingForEquip,
            SavingGearset
        }

        private SyncState currentState = SyncState.Idle;
        private Queue<int> gearsetsToProcess = new Queue<int>();
        private int currentTargetGearset = -1;
        private int waitFrames = 0;

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IChatGui chatGui,
            IFramework framework,
            IClientState clientState,
            IObjectTable objectTable)
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;
            this.ChatGui = chatGui;
            this.Framework = framework;
            this.ClientState = clientState;
            this.ObjectTable = objectTable;

            // Command registrieren
            this.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the RoleGearSync menu or starts directly (e.g., /syncgear healer)"
            });

            // In den Game-Loop für unsere State Machine einklinken
            this.Framework.Update += OnFrameworkUpdate;
            
            // In den UI-Loop von Dalamud einklinken
            this.PluginInterface.UiBuilder.Draw += DrawUI;

            // Hauptfenster registrieren (behebt die Warnung)
            this.PluginInterface.UiBuilder.OpenMainUi += () => isUiVisible = true;
            
            // Optional: Fenster über die Plugin-Einstellungen öffnen
            this.PluginInterface.UiBuilder.OpenConfigUi += () => isUiVisible = true;
        }

        public void Dispose()
        {
            // Event-Handler sauber abmelden
            this.PluginInterface.UiBuilder.Draw -= DrawUI;
            this.PluginInterface.UiBuilder.OpenMainUi -= () => isUiVisible = true;
            this.PluginInterface.UiBuilder.OpenConfigUi -= () => isUiVisible = true;
            this.Framework.Update -= OnFrameworkUpdate;
            this.CommandManager.RemoveHandler(CommandName);
        }

        private void OnCommand(string command, string args)
        {
            var role = args.ToLower().Trim();

            // Wenn keine Rolle angegeben wurde, UI umschalten
            if (string.IsNullOrEmpty(role))
            {
                isUiVisible = !isUiVisible;
                return;
            }

            // Ansonsten direkt ausführen
            ExecuteSync(role);
        }

        private void ExecuteSync(string role)
        {
            var sets = GetGearsetsForRole(role);

            if (sets.Count > 0)
            {
                StartSyncProcess(sets);
                ChatGui.Print($"[RoleGearSync] Starting optimization for {sets.Count} {role}-Set(s)...");
            }
            else
            {
                ChatGui.PrintError($"[RoleGearSync] No sets found for '{role}'.\nUse: healer, tank, melee, ranged, caster");
            }
        }

        private void DrawUI()
        {
            if (!isUiVisible) return;

            // Zeichnet ein einfaches Fenster, das sich an den Inhalt anpasst
            if (ImGui.Begin("RoleGearSync Menu", ref isUiVisible, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text("Choose a role to optimize:");
                ImGui.Separator();
                ImGui.Spacing();

                // Disable buttons if optimization is currently running
                if (currentState != SyncState.Idle)
                {
                    ImGui.BeginDisabled();
                }

                // UI Buttons
                if (ImGui.Button("Optimize Tanks", new System.Numerics.Vector2(200, 30))) ExecuteSync("tank");
                if (ImGui.Button("Optimize Healers", new System.Numerics.Vector2(200, 30))) ExecuteSync("healer");
                if (ImGui.Button("Optimize Melee", new System.Numerics.Vector2(200, 30))) ExecuteSync("melee");
                if (ImGui.Button("Optimize Ranged", new System.Numerics.Vector2(200, 30))) ExecuteSync("ranged");
                if (ImGui.Button("Optimize Caster", new System.Numerics.Vector2(200, 30))) ExecuteSync("caster");

                if (currentState != SyncState.Idle)
                {
                    ImGui.EndDisabled();
                    ImGui.Spacing();
                    ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.5f, 0.0f, 1.0f), "Optimization running...");
                }

                ImGui.End();
            }
        }

        private unsafe List<int> GetGearsetsForRole(string role)
        {
            var gearsetModule = RaptureGearsetModule.Instance();
            var matchingSets = new List<int>();

            var healers = new HashSet<byte> { 6, 24, 28, 33, 40 };
            var tanks = new HashSet<byte> { 1, 3, 19, 21, 32, 37 };
            var melee = new HashSet<byte> { 2, 4, 29, 20, 22, 30, 34, 39, 41 };
            var ranged = new HashSet<byte> { 5, 23, 31, 38 };
            var caster = new HashSet<byte> { 7, 26, 25, 27, 35, 36, 42 };

            HashSet<byte> targetRole = null;
            switch(role) {
                case "healer": targetRole = healers; break;
                case "tank": targetRole = tanks; break;
                case "melee": targetRole = melee; break;
                case "ranged": targetRole = ranged; break;
                case "caster": targetRole = caster; break;
            }

            if (targetRole == null) return matchingSets;

            for (int i = 0; i < 100; i++)
            {
                var gs = gearsetModule->GetGearset(i);
                if (gs != null && gs->ClassJob != 0)
                {
                    if (targetRole.Contains(gs->ClassJob))
                    {
                        matchingSets.Add(i);
                    }
                }
            }

            return matchingSets;
        }

        private void StartSyncProcess(List<int> gearsetIds)
        {
            if (currentState != SyncState.Idle)
            {
                ChatGui.PrintError("[RoleGearSync] A sync process is already running!");
                return;
            }

            gearsetsToProcess.Clear();
            foreach (var id in gearsetIds)
            {
                gearsetsToProcess.Enqueue(id);
            }

            currentState = SyncState.SwitchingJob;
        }

        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            if (currentState == SyncState.Idle || !ClientState.IsLoggedIn || ObjectTable.LocalPlayer == null)
                return;

            switch (currentState)
            {
                case SyncState.SwitchingJob:
                    if (gearsetsToProcess.Count == 0)
                    {
                        ChatGui.Print("[RoleGearSync] All jobs successfully optimized!");
                        currentState = SyncState.Idle;
                        return;
                    }

                    currentTargetGearset = gearsetsToProcess.Dequeue();
                    
                    var gearsetModule = RaptureGearsetModule.Instance();
                    gearsetModule->EquipGearset(currentTargetGearset);
                    
                    waitFrames = 60;
                    currentState = SyncState.WaitingForSwitch;
                    break;

                case SyncState.WaitingForSwitch:
                    waitFrames--;
                    if (waitFrames <= 0)
                        currentState = SyncState.SetupOptimization;
                    break;

                case SyncState.SetupOptimization:
                    var recommendModuleSetup = RecommendEquipModule.Instance();
                    recommendModuleSetup->SetupForClassJob((byte)ObjectTable.LocalPlayer.ClassJob.RowId);
                    
                    waitFrames = 15;
                    currentState = SyncState.WaitingForCalculation;
                    break;

                case SyncState.WaitingForCalculation:
                    waitFrames--;
                    if (waitFrames <= 0)
                        currentState = SyncState.EquippingGear;
                    break;

                case SyncState.EquippingGear:
                    var recommendModuleEquip = RecommendEquipModule.Instance();
                    recommendModuleEquip->EquipRecommendedGear();
                    
                    waitFrames = 30;
                    currentState = SyncState.WaitingForEquip;
                    break;
                    
                case SyncState.WaitingForEquip:
                    waitFrames--;
                    if (waitFrames <= 0)
                        currentState = SyncState.SavingGearset;
                    break;
                    
                case SyncState.SavingGearset:
                    var gearsetModuleUpdate = RaptureGearsetModule.Instance();
                    gearsetModuleUpdate->UpdateGearset(currentTargetGearset);
                    
                    currentState = SyncState.SwitchingJob;
                    break;
            }
        }
    }
}