using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
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
        
        // NEU: ObjectTable für Dawntrail API hinzugefügt
        private IObjectTable ObjectTable { get; init; }

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
            IObjectTable objectTable) // NEU im Konstruktor
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;
            this.ChatGui = chatGui;
            this.Framework = framework;
            this.ClientState = clientState;
            this.ObjectTable = objectTable; // NEU zugewiesen

            // Command registrieren
            this.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Synchronisiert Ausrüstung für eine bestimmte Rolle (z.B. /syncgear healer)"
            });

            // In den Game-Loop einklinken für unsere State Machine
            this.Framework.Update += OnFrameworkUpdate;
        }

        public void Dispose()
        {
            // WICHTIG: Event-Handler sauber abmelden (verhindert Memory Leaks und Crashes)
            this.Framework.Update -= OnFrameworkUpdate;
            this.CommandManager.RemoveHandler(CommandName);
        }

        private void OnCommand(string command, string args)
        {
            var role = args.ToLower().Trim();
            var sets = GetGearsetsForRole(role);

            if (sets.Count > 0)
            {
                StartSyncProcess(sets);
                ChatGui.Print($"[RoleGearSync] Starte Optimierung für {sets.Count} {role}-Set(s)...");
            }
            else
            {
                ChatGui.PrintError($"[RoleGearSync] Keine Sets für '{role}' gefunden.\nNutze: healer, tank, melee, ranged, caster");
            }
        }

        private unsafe List<int> GetGearsetsForRole(string role)
        {
            var gearsetModule = RaptureGearsetModule.Instance();
            var matchingSets = new List<int>();

            // FFXIV Job IDs (Klassen & Jobs)
            var healers = new HashSet<byte> { 6, 24, 28, 33, 40 }; // Druide, WHM, SCH, AST, SGE
            var tanks = new HashSet<byte> { 1, 3, 19, 21, 32, 37 }; // GLD, MRD, PLD, WAR, DRK, GNB
            var melee = new HashSet<byte> { 2, 4, 29, 20, 22, 30, 34, 39, 41 }; // PGL, LNC, ROG, MNK, DRG, NIN, SAM, RPR, VPR
            var ranged = new HashSet<byte> { 5, 23, 31, 38 }; // ARC, BRD, MCH, DNC
            var caster = new HashSet<byte> { 7, 26, 25, 27, 35, 36, 42 }; // THM, ACN, BLM, SMN, RDM, BLU, PCT

            HashSet<byte> targetRole = null;
            switch(role) {
                case "healer": targetRole = healers; break;
                case "tank": targetRole = tanks; break;
                case "melee": targetRole = melee; break;
                case "ranged": targetRole = ranged; break;
                case "caster": targetRole = caster; break;
            }

            if (targetRole == null) return matchingSets;

            // FFXIV erlaubt bis zu 100 Gearsets (Index 0 bis 99)
            for (int i = 0; i < 100; i++)
            {
                var gs = gearsetModule->GetGearset(i);
                
                // Prüfen, ob das Set existiert und eine Klasse zugewiesen ist (0 = Abenteurer/Leer)
                if (gs != null && gs->ClassJob != 0)
                {
                    if (targetRole.Contains(gs->ClassJob))
                    {
                        matchingSets.Add(i); // i ist der Index des Gearsets
                    }
                }
            }

            return matchingSets;
        }

        private void StartSyncProcess(List<int> gearsetIds)
        {
            if (currentState != SyncState.Idle)
            {
                ChatGui.PrintError("[RoleGearSync] Ein Sync-Prozess läuft bereits!");
                return;
            }

            gearsetsToProcess.Clear();
            foreach (var id in gearsetIds)
            {
                gearsetsToProcess.Enqueue(id);
            }

            currentState = SyncState.SwitchingJob;
        }

        // Diese Methode wird JEDEN FRAME aufgerufen (Ansatz A Logik)
        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            // Dawntrail API Update: Wir nutzen IsLoggedIn und das ObjectTable
            if (currentState == SyncState.Idle || !ClientState.IsLoggedIn || ObjectTable.LocalPlayer == null)
                return;

            switch (currentState)
            {
                case SyncState.SwitchingJob:
                    if (gearsetsToProcess.Count == 0)
                    {
                        ChatGui.Print("[RoleGearSync] Alle Jobs erfolgreich optimiert!");
                        currentState = SyncState.Idle;
                        return;
                    }

                    currentTargetGearset = gearsetsToProcess.Dequeue();
                    
                    // RaptureGearsetModule nutzen, um Job zu wechseln
                    var gearsetModule = RaptureGearsetModule.Instance();
                    gearsetModule->EquipGearset(currentTargetGearset);
                    
                    waitFrames = 60; // Etwas länger warten (~1 Sekunde), falls der Server-Ping hoch ist
                    currentState = SyncState.WaitingForSwitch;
                    break;

                case SyncState.WaitingForSwitch:
                    waitFrames--;
                    if (waitFrames <= 0)
                    {
                        currentState = SyncState.SetupOptimization;
                    }
                    break;

                case SyncState.SetupOptimization:
                    var recommendModuleSetup = RecommendEquipModule.Instance();
                    
                    // 1. Berechne im Hintergrund die optimale Ausrüstung
                    recommendModuleSetup->SetupForClassJob((byte)ObjectTable.LocalPlayer.ClassJob.RowId);
                    
                    waitFrames = 15; // WICHTIG: Gib dem Spiel Zeit, das Arsenal zu durchsuchen!
                    currentState = SyncState.WaitingForCalculation;
                    break;

                case SyncState.WaitingForCalculation:
                    waitFrames--;
                    if (waitFrames <= 0)
                    {
                        currentState = SyncState.EquippingGear;
                    }
                    break;

                case SyncState.EquippingGear:
                    var recommendModuleEquip = RecommendEquipModule.Instance();
                    
                    // 2. Wende die berechnete Ausrüstung an
                    recommendModuleEquip->EquipRecommendedGear();
                    
                    waitFrames = 30; // Warten, bis der Server die neuen Items bestätigt hat
                    currentState = SyncState.WaitingForEquip;
                    break;
                    
                case SyncState.WaitingForEquip:
                    waitFrames--;
                    if (waitFrames <= 0)
                    {
                        currentState = SyncState.SavingGearset;
                    }
                    break;
                    
                case SyncState.SavingGearset:
                    // RaptureGearsetModule nutzen, um das Set zu überschreiben/speichern
                    var gearsetModuleUpdate = RaptureGearsetModule.Instance();
                    gearsetModuleUpdate->UpdateGearset(currentTargetGearset);
                    
                    currentState = SyncState.SwitchingJob; // Nächster Job in der Queue
                    break;
            }
        }
    }
}