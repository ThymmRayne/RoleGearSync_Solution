using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Bindings.ImGui;
using System.Collections.Generic;
using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;

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
        private ICondition Condition { get; init; }
        private IToastGui ToastGui { get; init; }
        private Configuration Configuration { get; init; }

        // UI State
        private bool isUiVisible = false;
        private bool isConfigVisible = false;

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
        private DateTime waitTimer;
        private byte expectedClassJob;

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IChatGui chatGui,
            IFramework framework,
            IClientState clientState,
            IObjectTable objectTable,
            ICondition condition,
            IToastGui toastGui)
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;
            this.ChatGui = chatGui;
            this.Framework = framework;
            this.ClientState = clientState;
            this.ObjectTable = objectTable;
            this.Condition = condition;
            this.ToastGui = toastGui;

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
            this.PluginInterface.UiBuilder.OpenConfigUi += () => isConfigVisible = true;

            this.Configuration = this.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(this.PluginInterface);
        }

        public void Dispose()
        {
            // Event-Handler sauber abmelden
            this.PluginInterface.UiBuilder.Draw -= DrawUI;
            this.PluginInterface.UiBuilder.OpenMainUi -= () => isUiVisible = true;
            this.PluginInterface.UiBuilder.OpenConfigUi -= () => isConfigVisible = true;
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
            // Check if we are in combat or in a duty
            if (Condition[ConditionFlag.InCombat] || Condition[ConditionFlag.BoundByDuty])
            {
                ToastGui.ShowError($"RoleGearSync: Cannot optimize gear during combat or inside a duty.");
                return;
            }

            var sets = GetGearsetsForRole(role);

            if (sets.Count > 0)
            {
                StartSyncProcess(sets);
                ToastGui.ShowNormal($"RoleGearSync: Starting optimization for {sets.Count} {role}-Set(s)...");
            }
            else
            {
                ToastGui.ShowError($"RoleGearSync: No sets found for '{role}'.\nUse: healer, tank, melee, ranged, caster");
            }
        }
        private unsafe void DrawUI()
        {
            // -----------------------------------------
            // 1. HAUPTMENÜ (Öffnet sich bei /syncgear oder "Open")
            // -----------------------------------------
            if (isUiVisible)
            {
                if (ImGui.Begin("RoleGearSync Menu", ref isUiVisible, ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.Text("Choose a role to optimize:");
                    ImGui.Separator();
                    ImGui.Spacing();

                    if (currentState != SyncState.Idle)
                    {
                        ImGui.BeginDisabled();
                    }

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
                }
                ImGui.End();
            }

            // -----------------------------------------
            // 2. EINSTELLUNGEN (Öffnet sich beim Klick auf "Einstellungen")
            // -----------------------------------------
            if (isConfigVisible)
            {
                if (ImGui.Begin("RoleGearSync Settings", ref isConfigVisible, ImGuiWindowFlags.AlwaysAutoResize))
                {
                    ImGui.TextWrapped("Check the boxes to exclude specific gearsets from being optimized.");
                    ImGui.Spacing();

                    // Scrollbarer Bereich (Child-Window), damit das Hauptfenster nicht explodiert
                    if (ImGui.BeginChild("GearsetListScroll", new System.Numerics.Vector2(450, 350), true))
                    {
                        // Schicke Tabelle mit 3 Spalten und abwechselnden Zeilenfarben
                        if (ImGui.BeginTable("GearsetTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                        {
                            ImGui.TableSetupColumn("Ignore", ImGuiTableColumnFlags.WidthFixed, 50f);
                            ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthFixed, 40f);
                            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableHeadersRow();

                            var gearsetModule = RaptureGearsetModule.Instance();
                            for (int i = 0; i < 100; i++)
                            {
                                var gs = gearsetModule->GetGearset(i);
                                
                                if (gs != null)
                                {
                                    // Wir lesen den echten Namen deines Gearsets aus dem Speicher
                                    string setName = MemoryHelper.ReadStringNullTerminated((nint)gs->Name);
                                    
                                    // Ist der Name leer, existiert in diesem Slot kein Gearset
                                    if (string.IsNullOrEmpty(setName)) continue;

                                    bool isIgnored = this.Configuration.IgnoredGearsets.Contains(i);

                                    ImGui.TableNextRow();
                                    
                                    // Spalte 1: Checkbox
                                    ImGui.TableNextColumn();
                                    if (ImGui.Checkbox($"##ignore_{i}", ref isIgnored))
                                    {
                                        if (isIgnored) 
                                            this.Configuration.IgnoredGearsets.Add(i);
                                        else 
                                            this.Configuration.IgnoredGearsets.Remove(i);
                                        
                                        this.Configuration.Save(); 
                                    }

                                    // Spalte 2: Die Ingame Set-Nummer
                                    ImGui.TableNextColumn();
                                    ImGui.Text($"{i + 1}");

                                    // Spalte 3: Der echte Set-Name, den du vergeben hast
                                    ImGui.TableNextColumn();
                                    ImGui.Text($"{setName}");
                                }
                            }
                            ImGui.EndTable();
                        }
                        ImGui.EndChild();
                    }
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

            HashSet<byte>? targetRole = null;
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
                // Wenn das Gearset in der Blacklist ist, direkt überspringen
                if (this.Configuration.IgnoredGearsets.Contains(i)) continue;

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
                        ToastGui.ShowNormal("RoleGearSync: All jobs successfully optimized!");
                        currentState = SyncState.Idle;
                        return;
                    }
                    currentTargetGearset = gearsetsToProcess.Dequeue();
                                        
                    var gearsetModule = RaptureGearsetModule.Instance();
                    var gs = gearsetModule->GetGearset(currentTargetGearset);
                    
                    // Wir merken uns, in welchen Job wir gerade wechseln wollen
                    if (gs != null) expectedClassJob = gs->ClassJob; 
                    
                    gearsetModule->EquipGearset(currentTargetGearset);
                                        
                    // Maximal 2 Sekunden warten, bis der Job-Wechsel durch ist
                    waitTimer = DateTime.Now.AddSeconds(2); 
                    currentState = SyncState.WaitingForSwitch;
                    break;

                case SyncState.WaitingForSwitch:
                    // Prüfen, ob der Spieler den Job gewechselt hat
                    if (ObjectTable.LocalPlayer.ClassJob.RowId == expectedClassJob)
                    {
                        currentState = SyncState.SetupOptimization;
                    }
                    // Timeout-Schutz: Falls der Jobwechsel fehlschlägt (z.B. Inventar voll)
                    else if (DateTime.Now > waitTimer)
                    {
                        ChatGui.PrintError($"[RoleGearSync] Timeout while switching to gearset {currentTargetGearset}. Aborting.");
                        currentState = SyncState.Idle;
                    }
                    break;

                case SyncState.SetupOptimization:
                    var recommendModuleSetup = RecommendEquipModule.Instance();
                    recommendModuleSetup->SetupForClassJob((byte)ObjectTable.LocalPlayer.ClassJob.RowId);
                                        
                    // 250 Millisekunden warten, damit das Spiel Zeit hat, das beste Gear zu berechnen
                    waitTimer = DateTime.Now.AddMilliseconds(250);
                    currentState = SyncState.WaitingForCalculation;
                    break;

                case SyncState.WaitingForCalculation:
                    if (DateTime.Now >= waitTimer)
                        currentState = SyncState.EquippingGear;
                    break;

                case SyncState.EquippingGear:
                    var recommendModuleEquip = RecommendEquipModule.Instance();
                    recommendModuleEquip->EquipRecommendedGear();
                                        
                    // 500 Millisekunden warten, bis die Server-Anfrage für das Ausrüsten durch ist
                    waitTimer = DateTime.Now.AddMilliseconds(500);
                    currentState = SyncState.WaitingForEquip;
                    break;
                                    
                case SyncState.WaitingForEquip:
                    if (DateTime.Now >= waitTimer)
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