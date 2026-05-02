// ------------------------------------------------------------------------------
// Concept, Logic & Testing: Cycrow
// Code Refactoring & UI Implementation assisted by AI
// ------------------------------------------------------------------------------
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.Game;
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
        private string newProfileName = "";

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
        private Dictionary<int, short> oldItemLevels = new Dictionary<int, short>();
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
                // --- CHANGED: Updated HelpMessage for profile support ---
                HelpMessage = "Opens the menu or starts directly.\nUsage: /syncgear [role] [optional: profile] (e.g., /syncgear healer Raid)"
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
            var input = args.Trim();
            
            // Wenn keine Rolle angegeben wurde, UI umschalten
            if (string.IsNullOrEmpty(input))
            {
                isUiVisible = !isUiVisible;
                return;
            }

            // --- CHANGED: Split the input to get role and optional profile ---
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string role = parts[0].ToLower();
            string profile = parts.Length > 1 ? parts[1] : null;

            // Ansonsten direkt ausführen
            ExecuteSync(role, profile);
        }

        private unsafe void ExecuteSync(string role, string profile = null)
        {
            // --- NEW: Determine which profile to use ---
            string targetProfile = string.IsNullOrEmpty(profile) ? this.Configuration.ActiveProfile : profile;
            
            if (!this.Configuration.IgnoreProfiles.ContainsKey(targetProfile))
            {
                ChatGui.PrintError($"[RoleGearSync] The profile '{targetProfile}' does not exist!");
                return;
            }
            // -------------------------------------------  
            
            // Check if we are in combat or in a duty
            if (Condition[ConditionFlag.InCombat] || Condition[ConditionFlag.BoundByDuty])
            {
                ToastGui.ShowError($"RoleGearSync: Cannot optimize gear during combat or inside a duty.");
                return;
            }

            // --- NEW: Inventory space protection ---
            int freeSlots = 0;
            var invManager = InventoryManager.Instance();

            if (invManager != null)
            {
                // Check standard inventory bags (Inventory1 to Inventory4)
                InventoryType[] bags = { 
                    InventoryType.Inventory1, 
                    InventoryType.Inventory2, 
                    InventoryType.Inventory3, 
                    InventoryType.Inventory4 
                };

                foreach (var bag in bags)
                {
                    var container = invManager->GetInventoryContainer(bag);
                    if (container != null)
                    {
                        for (int i = 0; i < container->Size; i++)
                        {
                            var item = container->GetInventorySlot(i);
                            
                            // An ItemId of 0 means the slot is completely empty
                            if (item == null || item->ItemId == 0)
                            {
                                freeSlots++;
                            }
                        }
                    }
                }
                
                // If there are fewer than 3 free slots, abort the sync process
                if (freeSlots < 3)
                {
                    ChatGui.PrintError("[RoleGearSync] Optimization aborted: Not enough general inventory space. Please free up at least 3 slots.");
                    ToastGui.ShowError("RoleGearSync: Inventory almost full!");
                    return;
                }
            }

            // --- NEW: Spiritbond / Materia extraction warning ---
            var spiritbondInvManager = InventoryManager.Instance();
            if (spiritbondInvManager != null)
            {
                var equipContainer = spiritbondInvManager->GetInventoryContainer(InventoryType.EquippedItems);
                if (equipContainer != null)
                {
                    bool hasFullyBondedGear = false;
                    for (int i = 0; i < equipContainer->Size; i++)
                    {
                        var item = equipContainer->GetInventorySlot(i);
                        
                        // Spiritbond and Collectability share the same memory space. 10000 = 100.00%
                        if (item != null && item->ItemId != 0 && item->SpiritbondOrCollectability == 10000)
                        {
                            hasFullyBondedGear = true;
                            break;
                        }
                    }

                    if (hasFullyBondedGear)
                    {
                        ChatGui.Print("[RoleGearSync] TIP: Your currently equipped gear has 100% Spiritbond. Don't forget to extract Materia later!");
                    }
                }
            }
            // --- NEW: Gear Condition / Durability warning ---
            if (spiritbondInvManager != null)
            {
                var equipContainer = spiritbondInvManager->GetInventoryContainer(InventoryType.EquippedItems);
                if (equipContainer != null)
                {
                    bool hasBrokenGear = false;
                    for (int i = 0; i < equipContainer->Size; i++)
                    {
                        var item = equipContainer->GetInventorySlot(i);
                        
                        // Condition is stored as an ushort where 30000 = 100%. 3000 = 10%.
                        if (item != null && item->ItemId != 0 && item->Condition < 3000)
                        {
                            hasBrokenGear = true;
                            break;
                        }
                    }

                    if (hasBrokenGear)
                    {
                        // Text für das Chat-Protokoll
                        ChatGui.PrintError("[RoleGearSync] WARNING: Some of your equipped gear is below 10% durability. Don't forget to repair!");
                        
                        // Fette Warnung direkt auf dem Bildschirm!
                        ToastGui.ShowError("RoleGearSync: Gear durability below 10% - Repair soon!");
                    }
                }
            }
            // ------------------------------------------------
            var sets = GetGearsetsForRole(role, targetProfile);

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
                        
                        // --- NEW: Emergency Stop Button ---
                        ImGui.Spacing();
                        // Red button color styling
                        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.8f, 0.1f, 0.1f, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(1.0f, 0.2f, 0.2f, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.6f, 0.0f, 0.0f, 1.0f));
                        
                        if (ImGui.Button("ABORT / STOP", new System.Numerics.Vector2(200, 30)))
                        {
                            AbortSync();
                        }
                        
                        // Always pop the style colors so it doesn't affect other UI elements!
                        ImGui.PopStyleColor(3);
                        // ----------------------------------
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

                    bool reapplyGlamour = this.Configuration.ReapplyGlamour;
                    if (ImGui.Checkbox("Re-apply linked Glamour Plates after optimization", ref reapplyGlamour))
                    {
                        this.Configuration.ReapplyGlamour = reapplyGlamour;
                        this.Configuration.Save();
                    }
                    
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    // --- NEW: Profile Management UI ---
                    ImGui.Text("Ignore List Profile:");
                    
                    // Englische Erklärung direkt unter der Überschrift
                    ImGui.TextWrapped("Manage different ignore lists (e.g., for raiding or leveling).\nSelect a profile from the dropdown, or enter a new name and click 'Add Profile' to create one.");
                    ImGui.Spacing();
                    
                    var profiles = new List<string>(this.Configuration.IgnoreProfiles.Keys);
                    string active = this.Configuration.ActiveProfile;

                    if (ImGui.BeginCombo("##ProfileCombo", active))
                    {
                        foreach (var profile in profiles)
                        {
                            bool isSelected = (active == profile);
                            if (ImGui.Selectable(profile, isSelected))
                            {
                                this.Configuration.ActiveProfile = profile;
                                this.Configuration.Save();
                            }
                            if (isSelected) ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }

                    ImGui.InputText("##NewProfileName", ref newProfileName, 64);
                    ImGui.SameLine();
                    
                    if (ImGui.Button("Add Profile") && !string.IsNullOrWhiteSpace(newProfileName))
                    {
                        if (!this.Configuration.IgnoreProfiles.ContainsKey(newProfileName))
                        {
                            this.Configuration.IgnoreProfiles[newProfileName] = new HashSet<int>();
                            this.Configuration.ActiveProfile = newProfileName;
                            this.Configuration.Save();
                            newProfileName = ""; // Clear input field
                        }
                    }
                    // Englischer Tooltip für den "Add Profile"-Button (erscheint beim Drüberfahren)
                    if (ImGui.IsItemHovered()) 
                    {
                        ImGui.SetTooltip("Type a name in the text box and click here to create a new, empty profile.");
                    }

                    ImGui.SameLine();
                    
                    // Prevent deletion of the Default profile
                    ImGui.BeginDisabled(this.Configuration.ActiveProfile == "Default");
                    if (ImGui.Button("Delete Current"))
                    {
                        this.Configuration.IgnoreProfiles.Remove(this.Configuration.ActiveProfile);
                        this.Configuration.ActiveProfile = "Default";
                        this.Configuration.Save();
                    }
                    ImGui.EndDisabled();
                    // Englischer Tooltip für den "Delete Current"-Button
                    if (ImGui.IsItemHovered()) 
                    {
                        ImGui.SetTooltip("Delete the currently selected profile. The 'Default' profile cannot be deleted.");
                    }

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();
                    // ----------------------------------
                    ImGui.TextWrapped("Check the boxes to exclude specific gearsets from being optimized.");
                    ImGui.Spacing();

                    // Scrollbarer Bereich (Child-Window), damit das Hauptfenster nicht explodiert
                    if (ImGui.BeginChild("GearsetListScroll", new System.Numerics.Vector2(450, 350), true))
                    {
                        // Schicke Tabelle mit 4 Spalten und abwechselnden Zeilenfarben
                        if (ImGui.BeginTable("GearsetTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                        {
                            ImGui.TableSetupColumn("Ignore", ImGuiTableColumnFlags.WidthFixed, 50f);
                            ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthFixed, 40f);
                            ImGui.TableSetupColumn("Glamour", ImGuiTableColumnFlags.WidthFixed, 80f);
                            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableHeadersRow();

                            var gearsetModule = RaptureGearsetModule.Instance();
                            for (int i = 0; i < 100; i++)
                            {
                                var gs = gearsetModule->GetGearset(i);
                                
                                if (gs != null)
                                {
                                    // Wir lesen den echten Namen deines Gearsets aus dem Speicher
                                    string setName = gs->NameString;
                                    
                                    // Ist der Name leer, existiert in diesem Slot kein Gearset
                                    if (string.IsNullOrEmpty(setName)) continue;
                                    
                                    bool isIgnored = this.Configuration.IgnoreProfiles[this.Configuration.ActiveProfile].Contains(i);
                                    
                                    ImGui.TableNextRow();
                                    
                                    // --- Spalte 1: Checkbox ---
                                    ImGui.TableNextColumn();
                                    if (ImGui.Checkbox($"##ignore_{i}", ref isIgnored))
                                    {
                                        if (isIgnored)
                                            this.Configuration.IgnoreProfiles[this.Configuration.ActiveProfile].Add(i);
                                        else
                                            this.Configuration.IgnoreProfiles[this.Configuration.ActiveProfile].Remove(i);
                                            
                                        this.Configuration.Save();
                                    }

                                    // --- Spalte 2: Die Ingame Set-Nummer ---
                                    ImGui.TableNextColumn();
                                    ImGui.Text($"{i + 1}");

                                    // --- Spalte 3: Glamour Plate Dropdown ---
                                    ImGui.TableNextColumn();
                                    if (!this.Configuration.LinkedGlamourPlates.ContainsKey(i))
                                    {
                                        this.Configuration.LinkedGlamourPlates[i] = 0; // 0 = None
                                    }
                                    
                                    byte currentPlate = this.Configuration.LinkedGlamourPlates[i];
                                    string plateDisplay = currentPlate == 0 ? "None" : $"Plate {currentPlate}";
                                    
                                    ImGui.SetNextItemWidth(70f);
                                    if (ImGui.BeginCombo($"##glamour_{i}", plateDisplay))
                                    {
                                        // Option for "None"
                                        if (ImGui.Selectable("None", currentPlate == 0))
                                        {
                                            this.Configuration.LinkedGlamourPlates[i] = 0;
                                            this.Configuration.Save();
                                        }
                                        
                                        // Options for Plates 1-20
                                        for (byte p = 1; p <= 20; p++)
                                        {
                                            if (ImGui.Selectable($"Plate {p}", currentPlate == p))
                                            {
                                                this.Configuration.LinkedGlamourPlates[i] = p;
                                                this.Configuration.Save();
                                            }
                                        }
                                        ImGui.EndCombo();
                                    }

                                    // --- Spalte 4: Der echte Set-Name ---
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

        private unsafe List<int> GetGearsetsForRole(string role, string profileName)
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
                // --- CHANGED: Use the passed profileName ---
                if (this.Configuration.IgnoreProfiles[profileName].Contains(i)) continue;

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

        private unsafe void StartSyncProcess(List<int> gearsetIds)
        {
            if (currentState != SyncState.Idle)
            {
                ChatGui.PrintError("[RoleGearSync] A sync process is already running!");
                return;
            }

            gearsetsToProcess.Clear();
            oldItemLevels.Clear(); // --- NEW: Clear old memory ---
            
            var gearsetModule = RaptureGearsetModule.Instance(); // --- NEW ---

            foreach (var id in gearsetIds)
            {
                gearsetsToProcess.Enqueue(id);
                
                // --- NEW: Save the old item level before we do anything ---
                var gs = gearsetModule->GetGearset(id);
                if (gs != null)
                {
                    oldItemLevels[id] = gs->ItemLevel;
                }
                // ----------------------------------------------------------
            }

            currentState = SyncState.SwitchingJob;
        }
        // --- NEW: Emergency Stop Logic ---
        private void AbortSync()
        {
            if (currentState != SyncState.Idle)
            {
                gearsetsToProcess.Clear();
                oldItemLevels.Clear(); // Clears the memory from our previous feature
                currentState = SyncState.Idle;
                
                ChatGui.PrintError("[RoleGearSync] Optimization manually aborted by user!");
                ToastGui.ShowError("RoleGearSync: Emergency Stop!");
            }
        }
        // ---------------------------------
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
                        
                        // --- NEW: Print detailed report to chat ---
                        ChatGui.Print("[RoleGearSync] Optimization Report:");
                        var gearsetModuleReport = RaptureGearsetModule.Instance();
                        
                        foreach (var kvp in oldItemLevels)
                        {
                            var gsReport = gearsetModuleReport->GetGearset(kvp.Key);
                            if (gsReport != null)
                            {
                                string setName = gsReport->NameString;
                                short oldILvl = kvp.Value;
                                short newILvl = gsReport->ItemLevel;
                                
                                if (oldILvl != newILvl)
                                {
                                    ChatGui.Print($"[+] {setName} (Set {kvp.Key + 1}): iLvl {oldILvl} -> {newILvl}");
                                }
                                else
                                {
                                    ChatGui.Print($"[-] {setName} (Set {kvp.Key + 1}): iLvl unchanged ({newILvl})");
                                }
                            }
                        }
                        // ------------------------------------------

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
                    // --- NEW: Empty slot warning (e.g., missing rings) ---
                    var invManager = InventoryManager.Instance();
                    if (invManager != null)
                    {
                        var equipContainer = invManager->GetInventoryContainer(InventoryType.EquippedItems);
                        if (equipContainer != null)
                        {
                            // Indices: 0=MainHand, 2=Head, 3=Body, 4=Hands, 6=Legs, 7=Feet, 
                            // 8=Ears, 9=Neck, 10=Wrists, 11=RingRight, 12=RingLeft
                            // We intentionally skip 1 (OffHand) and 13 (SoulCrystal).
                            int[] slotsToCheck = { 0, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12 };
                            bool hasEmptySlot = false;

                            foreach (int slot in slotsToCheck)
                            {
                                var item = equipContainer->GetInventorySlot(slot);
                                if (item == null || item->ItemId == 0)
                                {
                                    hasEmptySlot = true;
                                    break;
                                }
                            }

                            if (hasEmptySlot)
                            {
                                ChatGui.PrintError($"[RoleGearSync] WARNING: Empty gear slot detected on Set {currentTargetGearset + 1}! Check your rings.");
                            }
                        }
                    }
                    // ------------------------------------------------------
                    var gearsetModuleUpdate = RaptureGearsetModule.Instance();
                    gearsetModuleUpdate->UpdateGearset(currentTargetGearset);
                                         
                    // --- CHANGED: Apply Glamour Plate ---
                    if (this.Configuration.ReapplyGlamour)
                    {
                        // Check if we have a specific plate linked in our plugin
                        if (this.Configuration.LinkedGlamourPlates.TryGetValue(currentTargetGearset, out byte linkedPlate) && linkedPlate > 0)
                        {
                            // Link the plate to the gearset internally (0-19 index for plates 1-20)
                            gearsetModuleUpdate->LinkGlamourPlate(currentTargetGearset, (byte)(linkedPlate - 1));
                        }
                        
                        // Equip the set again to trigger the glamour application
                        gearsetModuleUpdate->EquipGearset(currentTargetGearset);
                    }
                    // ------------------------------------
                                        
                    currentState = SyncState.SwitchingJob;
                    break;
            }
        }
    }
}