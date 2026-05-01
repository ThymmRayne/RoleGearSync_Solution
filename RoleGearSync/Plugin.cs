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
            OptimizingGear,
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
            if (args.ToLower() == "healer")
            {
                // Beispiel: IDs der Heiler-Gearsets (Diese müssten wir später dynamisch auslesen)
                // Weißmagier, Gelehrter, Astrologe, Weiser
                StartSyncProcess(new List<int> { 1, 2, 3, 4 });
                ChatGui.Print("[RoleGearSync] Starte Optimierung für Heiler...");
            }
            else
            {
                ChatGui.PrintError("[RoleGearSync] Unbekannte Rolle. Nutze z.B. '/syncgear healer'.");
            }
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
                    
                    waitFrames = 30; // Wir warten kurz (~0.5 Sekunden bei 60fps), bis der Jobwechsel durch ist
                    currentState = SyncState.WaitingForSwitch;
                    break;

                case SyncState.WaitingForSwitch:
                    waitFrames--;
                    if (waitFrames <= 0)
                    {
                        currentState = SyncState.OptimizingGear;
                    }
                    break;

                case SyncState.OptimizingGear:
                    // RecommendEquipModule aufrufen
                    var recommendModule = RecommendEquipModule.Instance();
                    
                    // 1. Berechnet im Hintergrund die optimale Ausrüstung (NEUE METHODE FÜR DAWNTRAIL)
                    recommendModule->SetupForClassJob((byte)ObjectTable.LocalPlayer.ClassJob.RowId);
                    
                    // 2. Wendet die berechnete Ausrüstung auf den Charakter an (NEUE METHODE FÜR DAWNTRAIL)
                    recommendModule->Equip();
                    
                    waitFrames = 10; // Kurz warten, bis das Spiel die Items angelegt hat
                    currentState = SyncState.SavingGearset;
                    break;
                    
                case SyncState.SavingGearset:
                    waitFrames--;
                    if (waitFrames <= 0)
                    {
                        // RaptureGearsetModule nutzen, um das Set zu überschreiben/speichern
                        var gearsetModuleUpdate = RaptureGearsetModule.Instance();
                        gearsetModuleUpdate->UpdateGearset(currentTargetGearset);
                        
                        currentState = SyncState.SwitchingJob; // Nächster Job in der Queue
                    }
                    break;
            }
        }
    }
}