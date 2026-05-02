using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace RoleGearSync
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        // --- NEW: Profile System ---
        public Dictionary<string, HashSet<int>> IgnoreProfiles { get; set; } = new Dictionary<string, HashSet<int>>() 
        { 
            { "Default", new HashSet<int>() } 
        };
        public string ActiveProfile { get; set; } = "Default";
        
        // Keep for backwards compatibility and migration
        public HashSet<int>? IgnoredGearsets { get; set; }
        // ---------------------------

        public bool ReapplyGlamour { get; set; } = true;
        
        // --- NEW: Glamour Plate Linking ---
        // Dictionary linking Gearset ID to Glamour Plate ID (1-20). Value 0 means no plate linked.
        public Dictionary<int, byte> LinkedGlamourPlates { get; set; } = new Dictionary<int, byte>();
        // ----------------------------------   

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
            
            // --- NEW: Migration logic for old data ---
            if (this.IgnoredGearsets != null && this.IgnoredGearsets.Count > 0)
            {
                this.IgnoreProfiles["Default"] = new HashSet<int>(this.IgnoredGearsets);
                this.IgnoredGearsets = null; // Clear old data
                this.Save();
            }
            
            // Failsafe: Ensure active profile exists
            if (!this.IgnoreProfiles.ContainsKey(this.ActiveProfile))
            {
                this.ActiveProfile = "Default";
            }
            // -----------------------------------------
        }

        public void Save()
        {
            this.pluginInterface!.SavePluginConfig(this);
        }
    }
}