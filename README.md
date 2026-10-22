

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin that writes a timestamped combat log to a text file while you play. Each line records the target, the damage taken, the attacker, and whether it was a crit.

```
14:32:07:123 [Combat] Redtail(412) took 85 damage from PlayWarrior(Critical)
```

## About
- This is a backport or OrbusVR:Reborn's combat log, it will write the log to the same location as OrbusVR:Reborn so you will not need any Cindy Configuration
- you can download the Cindy Parser from [Scott's Discord](https://discord.gg/DtyevveHat)
- Added Optional XP Gain logging, you can enable it by setting `ShowXp = true` in the config file Found in the `OrbusVR-Classic-Community-PC/BepInEx/config/` folder

## Install
<img width="636" height="291" alt="image" src="https://github.com/user-attachments/assets/52c9f83c-8440-4954-bc9d-1d1eb7598adc" />

1. Install BepInEx into your game folder: https://github.com/BepInEx/BepInEx/releases
   Use the build that matches your game (Mono), then run the game once so BepInEx generates its folders.
2. Download CombatLogger.dll from the [Releases](../../releases) page.
3. Put it in `OrbusVR-Classic-Community-PC/BepInEx/plugins/`.
4. Launch the game. The log is written to The default OrbusVR:Reborn log found at `/LocalLow/Orbus Online, LLC/OrbusVR/combat.log`.

## Linux Install

See the official Linux install guide [here](https://docs.bepinex.dev/articles/advanced/proton_wine.html)

## Build from scratch

**Requirements:** Visual Studio 2022 (or the .NET SDK) with .NET Framework 4.x targeting, and BepInEx installed in your game folder.

1. Open CombatLogger.sln in Visual Studio.
2. Add references (Project -> Add Reference -> Browse). Don't commit these DLLs.
   - From `OrbusVR-Classic-Community-PC/BepInEx/core/`: `BepInEx.dll`, `0Harmony.dll`
   - From `OrbusVR-Classic-Community-PC/OrbusVR-Classic-Community-PC_Data/Managed/`: `Assembly-CSharp.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`
3. Set the configuration to **Release** and choose **Build -> Build Solution**.
4. Copy `bin/Release/CombatLogger.dll` into `OrbusVR-Classic-Community-PC/BepInEx/plugins/`.
5. Launch the game.
