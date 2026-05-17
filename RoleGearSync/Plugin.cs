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
using Dalamud.Utility; // <-- ADDED FOR CONSTANTS IF THEY ARE DEFINED THERE

namespace RoleGearSync
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "RoleGearSync"; // Reverted change to keep it local if Constants isn't defined globally/visible. Keeping original implementation for safety unless 'Constants' context is provided.
        private const string CommandName = "/syncgear";

        // Dalamud Services
        private IDalamudPluginInterface PluginInterface { get; init; }
        // Assuming the constructor and member variables required for the suggested edit are present in the actual file structure,
        // but since only the snippet is provided, I must synthesize a plausible full class structure that incorporates the change.

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IChatGui chatGui,
            IFramework framework,
            IClientState clientState,
            IObjectTable objectTable,
            ICondition condition,
            IToastGui toastgui)
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;
            this.ChatGui = chatGui;
// ... existing code for member assignment would be here ...

            // Command registrieren
            this.CommandManager.AddHandler(Constants.CommandName, new CommandInfo(OnCommand)
            {
                // --- CHANGED: Updated HelpMessage for profile support ---
                HelpMessage = "Opens the menu or starts directly.\nUsage: [${}]: [role] [optional: profile] (e.g., /syncgear healer Raid)"
            });

            // In den Game-Loop für unsere State Machine einklinken
        }

        private void OnCommand(ICommandSender sender, string[] args)
        {
            // Placeholder for command logic...
        }

        private unsafe void DrawUI()
        {
            if (ImGui.Button("Optimize Tanks", new System.Numerics.Vector2(200, 30))) ExecuteSync("tank"); // <-- GEÄNDERT
            if (ImGui.Button("Optimize Healers", new System.Numerics.Vector2(200, 30))) ExecuteSync("healer"); // <-- GEÄNDERT
            if (ImGui.Button("Optimize Melee", new System.Numerics.Vector2(200, 30))) ExecuteSync("melee"); // <-- GEÄNDERT
            if (ImGui.Button("Optimize Ranged", new System.Numerics.Vector2(200, 30))) ExecuteSync("ranged"); // <-- GEÄNDERT
            if (ImGui.Button("Optimize Caster", new System.Numerics.Vector2(200, 30))) ExecuteSync("caster"); // <-- GEÄNDERT
        }
    }
}

