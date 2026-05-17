using System;

amespace RoleGearSync
{
    /// <summary>
    /// Enthält alle zentralen, hartkodierten Konstanten des Plugins.
    /// Änderungen an diesen Werten müssen global berücksichtigt werden.
    /// </summary>
    public static class Constants
    {
        // Plugin-Informationen
        public const string PluginName = "RoleGearSync";
        public const string CommandName = "/syncgear";

        // Job-Rollen (für einfache Vergleiche)
        public static readonly HashSet<string> ValidRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { 
            "healer", "tank", "melee", "ranged", "caster"
        };

        // Hardcoded Gearset Job-IDs (nur zur Dokumentation der Abhängigkeit)
        public static readonly Dictionary<string, HashSet<byte>> RoleJobAssignments = new Dictionary<string, HashSet<byte>>() 
        {
            { "healer", new HashSet<byte> { 6, 24, 28, 33, 40 } },
            { "tank",   new HashSet<byte> { 1, 3, 19, 21, 32, 37 } },
            { "melee",  new HashSet<byte> { 2, 4, 29, 20, 22, 30, 34, 39, 41 } },
            { "ranged", new HashSet<byte> { 5, 23, 31, 38 } },
            { "caster", new HashSet<byte> { 7, 26, 25, 27, 35, 36, 42 } }
        };
    }
}