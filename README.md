# RoleGearSync

**Synchronizes gear sets for all jobs within a specific role in Final Fantasy XIV.**

RoleGearSync is a Quality-of-Life (QoL) plugin for Final Fantasy XIV (XIVLauncher/Dalamud) designed specifically for "Omni-100" players. It allows you to update and save the "Optimize Gear" function for all jobs of a role (e.g., all Healers or all Tanks) with just a single click.

## Features
- **One-Click Optimization:** Automatically switches through all jobs of a role, runs the gear optimization, and saves the gearset.
- **Role-Based Syncing:** Support for Tanks, Healers, Melee DPS, Ranged DPS, and Casters.
- **Smart Ignore List:** Exclude specific gearsets (e.g., Ultimate BiS or Leveling sets) from the synchronization process via a dedicated settings menu.
- **Modern UI:** Clean ImGui-based interface with a searchable and scrollable gearset list.
- **Safe Execution:** Built-in security checks to prevent execution during combat or inside duties.
- **Toast Notifications:** On-screen feedback using the native FFXIV toast system.

## Installation
Currently, this plugin is in development. To install it manually:
1. Open the Dalamud Settings in-game (`/xlsettings`).
2. Go to the **Experimental** tab.
3. Add the following URL to your Custom Plugin Repositories:
   `https://raw.githubusercontent.com/ThymmRayne/RoleGearSync/main/repo.json`
4. Search for "RoleGearSync" in the Plugin Installer (`/xlplugins`).

## Usage
- Type `/syncgear` to open the main menu.
- Use `/syncgear <role>` (e.g., `/syncgear tank`) to start the optimization directly.
- Access the **Settings** via the Dalamud Plugin Installer or the button in the main menu to manage your ignored gearsets.

## AI Usage Disclosure
In accordance with the Dalamud AI Usage Policy:
- **Concept, Logic & Testing:** Created by Cycrow.
- **Code Assistance:** Parts of the C# implementation, specifically the State Machine refactoring and ImGui table layouts, were developed with the assistance of AI (Gemini).
- The plugin adheres to the official [Dalamud Code of Conduct](https://github.com/goatcorp/Dalamud/blob/master/CODE_OF_CONDUCT.md).

## Acknowledgements
- [Dalamud](https://github.com/goatcorp/Dalamud) for the amazing plugin framework.
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) for providing the game's internal mappings.
