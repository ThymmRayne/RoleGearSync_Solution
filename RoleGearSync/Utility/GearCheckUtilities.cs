using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Services;
using System;

namespace RoleGearSync
{
    public static class GearCheckUtilities
    {
        /// <summary>
        /// Prüft den aktuellen Zustand der ausgerüsteten Ausrüstung auf kritische Punkte: 
        /// Spiritbond, Durability und Materia. Gibt eine Zusammenfassung der Ergebnisse zurück.
        /// </summary>
        /// <param name="chatGui">Service für Chat-Nachrichten.</param>
        /// <param name="toastGui">Service für grafisches UI-Feedback.</param>
        /// <returns>True, wenn keine kritischen Fehler gefunden wurden; sonst False.</returns>
        public static bool CheckGearStatus(IChatGui chatGui, IToastGui toastGui)
        {
            var spiritbondInvManager = InventoryManager.Instance();

            if (spiritbondInvManager == null)
            {
                return true; // Kann nicht geprüft werden, aber kritisch genug? Für jetzt ignorieren.
            }

            bool hasFullyBondedGear = false;
            bool hasBrokenGear = false;
            bool missingMateria = false;
            var equipContainer = spiritbondInvManager->GetInventoryContainer(InventoryType.EquippedItems);

            if (equipContainer == null) return true;


            // Initialisierung der Status-Flags
            var statusReport = new System.Text.StringBuilder();

            for (int i = 0; i < equipContainer->Size; i++)
            {
                var item = equipContainer->GetInventorySlot(i);
                if (item == null || item->ItemId == 0) continue;


                // --- Spiritbond Check ---
                if (item->SpiritbondOrCollectability == 10000)
                {
                    hasFullyBondedGear = true;
                }

                // --- Durability Check ---
                // Condition is stored as an ushort where 30000 = 100%. 3000 = 10%.
                if (item->Condition < 3000)
                {
                    hasBrokenGear = true;
                }

                // --- Materia Check ---
                // Überprüfen, ob der erste Eintrag in einem potenziellen Materia-Array 0 ist.
                // Dies ist eine starke Annahme und muss im Spiel kontextualisiert werden, aber für diesen Scope reicht es.
                if (item->Materia?.Length > 0 && item->Materia[0] == 0)
                {
                    missingMateria = true;
                }
            }

            // --- Report-Generierung und Feedback ---
            bool overallSuccess = true;

            if (hasFullyBondedGear)
            {
                statusReport.AppendLine("[RoleGearSync] TIP: Your currently equipped gear has 100% Spiritbond. Don't forget to extract Materia later!");
                toastGui.ShowError("RoleGearSync: Gear hat 100% Spiritbond. Materia extrahieren vergessen?"); // Lokalisierung

                // Wir setzen overallSuccess auf false, weil es ein "Gewinn" ist, aber wir wollen, dass der Benutzer das liest!
                overallSuccess = true; 
            }

            if (hasBrokenGear)
            {
                chatGui.PrintError("[RoleGearSync] WARNING: Einige Ausrüstungsgegenstände haben unter 10% Beständigkeit. Vergessen Sie nicht zu reparieren!");
                toastGui.ShowError("RoleGearSync: Gear-Beständigkeit < 10% - Bald warten!");
                overallSuccess = false;
            }

            if (missingMateria)
            {
                chatGui.Print("[RoleGearSync] TIP: Einige Ausrüstungsgegenstände haben völlig leere Materia-Slots!");
                toastGui.ShowNormal("RoleGearSync: Einiger Gear hat leere Materia Slots!"); // Lokalisierung
            }

            if (overallSuccess && statusReport.Length == 0)
            {
                 // Falls nichts passiert ist und keine Warnung ausgegeben wurde, melden wir das Stillsitzen nicht.
                 return true;
            }
            else if (!string.IsNullOrEmpty(statusReport.ToString()))
            {
                chatGui.Print(statusReport.ToString());
            }

            return overallSuccess;
        }
    }
}
