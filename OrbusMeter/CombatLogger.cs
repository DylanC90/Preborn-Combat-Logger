using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using MagicalActual;
using UnityEngine;

[BepInPlugin("local.orbus.combatlogger", "Combat Logger", "1.1")]
public class CombatLogger : BaseUnityPlugin
{
    public static string LogPath;

    void Awake()
    {
        LogPath = GetCombatLogPath();

        try
        {
            string directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            CombatLogWriter.Open(LogPath);
        }
        catch (Exception e)
        {
            Logger.LogWarning("Couldn't prepare log file: " + e.Message);
            LogPath = Path.Combine(Paths.GameRootPath, "combat.log");

            try
            {
                CombatLogWriter.Open(LogPath);
            }
            catch (Exception fallbackError)
            {
                Logger.LogError("Couldn't open combat log: " + fallbackError.Message);
                return;
            }
        }

        CombatLogWriter.Start();
        try
        {
            new Harmony("local.orbus.combatlogger").PatchAll();
        }
        catch (Exception e)
        {
            Logger.LogError("Patch failed: " + e);
        }

        Logger.LogInfo("Combat Logger loaded, logging to " + LogPath);
    }

    void OnApplicationQuit()
    {
        CombatLogWriter.Stop();
    }

    static string GetCombatLogPath()
    {
        string userProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        if (string.IsNullOrEmpty(userProfile))
            return Path.Combine(Paths.GameRootPath, "combat.log");

        return userProfile
            + Path.DirectorySeparatorChar + "AppData"
            + Path.DirectorySeparatorChar + "LocalLow"
            + Path.DirectorySeparatorChar + "Orbus Online, LLC"
            + Path.DirectorySeparatorChar + "OrbusVR"
            + Path.DirectorySeparatorChar + "combat.log";
    }
}


public static class CombatLog
{
    public static void Write(string body)
    {
        CombatLogWriter.WriteLine(body);
    }

    //Combat event from the damage patch (Runs every instance of combat and Potentially can cause lag on slower Cpu's).
    public static void LogDamage(ushort targetGuid, ushort attackerGuid, int damage, bool crit)
    {
        long now = DateTime.UtcNow.Ticks;

        CombatLogWriter.EnqueueCombat(
            now,
            NameCache.Resolve(targetGuid, now), targetGuid,
            NameCache.Resolve(attackerGuid, now), attackerGuid,
            damage, crit);
    }

    //Called from the UpdateHealthBar postfix, which fires very often so i use Caching to save resources.
    public static void CacheMonsterName(Monster monster)
    {
        NameCache.CacheMonsterName(monster);
    }
}