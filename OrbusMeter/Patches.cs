using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using EckTechGames.FloatingCombatText;
using ExtUI;
using HarmonyLib;
using MagicalActual;
using UnityEngine;

// Cache monster names when their health bar updates.

[HarmonyPatch(typeof(Monster), "UpdateHealthBar")]
class MonsterNamePatch
{
    static void Postfix(Monster __instance)
    {
        CombatLog.CacheMonsterName(__instance);
    }
}


// Tracks the entity receiving a network component event.

class NetEntityContext
{
    [ThreadStatic]
    public static NetEntity current;
}

[HarmonyPatch(typeof(NetEntity), "TriggerListenerForMethod")]
class NetEntityPatch
{
    static void Prefix(NetEntity __instance, out NetEntity __state)
    {
        __state = NetEntityContext.current;
        NetEntityContext.current = __instance;
    }

    // Finalizer restores context even if the original method throws.
    static Exception Finalizer(Exception __exception, NetEntity __state)
    {
        NetEntityContext.current = __state;
        return __exception;
    }
}


// ---------- Zone-change logging ----------

[HarmonyPatch(typeof(OrbusNetManager), "handleWsTextMsg")]
class ZoneChangePatch
{
    const string prefix = "clientbegin:";

    static void Postfix(string message)
    {
        try
        {
            if (string.IsNullOrEmpty(message) ||
                !message.StartsWith(prefix, StringComparison.Ordinal))
                return;

            int zoneStart = message.IndexOf(':', prefix.Length);
            if (zoneStart < 0)
                return;

            zoneStart++;
            int zoneEnd = message.IndexOf(':', zoneStart);

            string zone = zoneEnd < 0
                ? message.Substring(zoneStart)
                : message.Substring(zoneStart, zoneEnd - zoneStart);

            CombatLog.Write("[System] Entered zone: " + zone);
        }
        catch
        {
        }
    }
}


// ---------- Damage component: Monster method ID 6 (and optional XP: method ID 7) ----------

[HarmonyPatch]
class ComponentListenerPatch
{
    static Type listenerType;
    static Func<object, byte> getMethodId;
    static Func<object, List<string>> getTypes;

    static List<string> validatedTypes;
    static List<string> validatedXpTypes;

    static bool Prepare()
    {
        try
        {
            listenerType = AccessTools.TypeByName("MagicalActual.ComponentListener");
            if (listenerType == null)
                return false;

            var methodField = AccessTools.Field(listenerType, "methodId");
            var typesField = AccessTools.Field(listenerType, "types");

            if (methodField == null || typesField == null ||
                methodField.FieldType != typeof(byte) ||
                typesField.FieldType != typeof(List<string>))
                return false;

            getMethodId = (Func<object, byte>)BuildGetter(typeof(Func<object, byte>), methodField);
            getTypes = (Func<object, List<string>>)BuildGetter(typeof(Func<object, List<string>>), typesField);

            return AccessTools.Method(listenerType, "doRead") != null;
        }
        catch
        {
            return false;
        }
    }

    static MethodBase TargetMethod()
    {
        return AccessTools.Method(listenerType, "doRead");
    }

    static Delegate BuildGetter(Type delegateType, FieldInfo field)
    {
        var dm = new DynamicMethod(
            "get_" + field.Name,
            field.FieldType,
            new[] { typeof(object) },
            listenerType,
            true); // skip visibility so private fields work

        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, listenerType);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);

        return dm.CreateDelegate(delegateType);
    }

    static bool TypesMatch(List<string> types)
    {
        return types != null
            && types.Count == 3
            && string.Equals(types[0], "ushort")
            && string.Equals(types[1], "int")
            && string.Equals(types[2], "bool");
    }

    static void ReadXpPacket(object listener, BinaryReader reader)
    {
        //Validate the signature once per distinct list
        var types = getTypes(listener);
        if (!ReferenceEquals(types, validatedXpTypes))
        {
            if (types == null || types.Count != 2 || types[0] != "int" || types[1] != "ushort")
                return;
            validatedXpTypes = types;
        }

        //Peek at the payload without consuming it
        Stream s = reader.BaseStream;
        long position = s.Position;
        int amount = reader.ReadInt32();
        s.Position = position;

        if (amount > 0)
            CombatLog.LogLoot(amount);
    }

    static void Prefix(object __instance, BinaryReader reader)
    {
        try
        {
            byte id = getMethodId(__instance);

            //Nearly every doRead exits here after one compiled field read.
            if (id != 6)
            {
                if (id == 7 && CombatLogger.ShowXp)
                    ReadXpPacket(__instance, reader);
                return;
            }

            //Validate the signature once per distinct list instead of every hit.
            var types = getTypes(__instance);
            if (!ReferenceEquals(types, validatedTypes))
            {
                if (!TypesMatch(types))
                    return;

                validatedTypes = types;
            }

            //Peek at the payload without consuming it.
            Stream s = reader.BaseStream;
            long position = s.Position;

            ushort attackerGuid = reader.ReadUInt16();
            int damage = reader.ReadInt32();
            bool crit = reader.ReadBoolean();

            s.Position = position;

            var entity = NetEntityContext.current;
            ushort targetGuid = entity != null ? entity.guid : (ushort)0;

            CombatLog.LogDamage(targetGuid, attackerGuid, damage, crit);
        }
        catch (Exception e)
        {
            CombatLog.Write("[Combat] (error) " + e.Message);
        }
    }
}


[HarmonyPatch(typeof(Fisher), "showCatchReward")]
class FishWaterLogPatch
{
    // ---------- Scenario table ----------
    class Scn
    {
        public string fish; public int seed; public int window;
        public string time;      // null = any, "late" = >0.75, "early" = <0.25
        public int[] weather;    // null = any
        public Scn(string f, int s, int w, string t, int[] wx)
        { fish = f; seed = s; window = w; time = t; weather = wx; }
    }

    static readonly int[] CLEAR = { 0 };
    static readonly int[] DRY = { 0, 1 };
    static readonly int[] WET = { 2, 3 };
    static readonly int[] CLEARWET = { 0, 2, 3 };

    static readonly List<Scn> scenarios = new List<Scn>
    {
        new Scn("Damage",0,0,"late",null), new Scn("Damage",1,1,"late",null), new Scn("Damage",2,2,"late",null),
        new Scn("Damage",3,3,"late",null), new Scn("Damage",4,4,"late",null),
        new Scn("Damage",0,1,"early",CLEAR), new Scn("Damage",1,2,"early",CLEAR), new Scn("Damage",2,3,"early",CLEAR),
        new Scn("Damage",3,4,"early",CLEAR), new Scn("Damage",4,0,"early",CLEAR),
        new Scn("Frosted",0,2,null,WET), new Scn("Frosted",1,3,null,WET), new Scn("Frosted",3,0,null,CLEARWET),
        new Scn("Blimp",0,1,null,DRY), new Scn("Blimp",2,3,null,DRY), new Scn("Blimp",4,4,null,DRY),
    };

    static readonly string[] fishOrder = { "Frosted", "Blimp", "Damage" };

    // ---------- Reflection ----------
    static readonly System.Reflection.FieldInfo bobberField =
        AccessTools.Field(typeof(Fisher), "currentBobber");

    static readonly System.Reflection.FieldInfo weatherField =
        AccessTools.Field(typeof(OrbusNetManager), "currentWeatherState");

    // ---------- Patch ----------
    static void Prefix(Fisher __instance, ushort rewardType, float rewardWeight, bool didCatchFish)
    {
        if (!CombatLogger.ShowFishing) return;
        try
        {
            if (!__instance.isLocallyControlled) return;

            var bobber = bobberField.GetValue(__instance) as GameObject;
            Vector3 p = bobber != null ? bobber.transform.position : __instance.transform.position;

            var water = FindWater(p);

            var item = InterfaceAPI.GetItemById(rewardType);
            string rolled = item != null ? item.name : "id " + rewardType;

            // Basic catch line
            string waterInfo;
            if (water != null)
            {
                int sum = 0;
                foreach (char c in water.gameObject.name) sum += c;
                waterInfo = "water='" + water.gameObject.name + "'" +
                            " table=[" + water.fishTable + "]" +
                            " rare=" + water.canContainRareFish +
                            " seed=" + (sum % 5);
            }
            else waterInfo = "water=<none found>";

            int window; double frac;
            Clock(out window, out frac);
            int weather = CurrentWeather();

            CombatLog.Write("[Fishing] " + waterInfo +
                            " | rolled=" + rolled + " (" + rewardWeight.ToString("F1") + " lb)" +
                            " caught=" + didCatchFish +
                            " | win=" + window + " time=" + frac.ToString("F2") +
                            " weather=" + WeatherName(weather) + "(" + weather + ")");

            // Full fish table for this water
            if (water == null) return;
            int seed = SlotOf(water.gameObject.name);

            if (!water.canContainRareFish)
            {
                CombatLog.Write("[FishTable] no rare fish possible here");
                return;
            }

            foreach (var f in fishOrder)
                CombatLog.Write("[FishTable] " + Evaluate(f, seed, window, frac, weather));

            CombatLog.Write("[FishTable] Seeds that work right now: " +
                string.Join("  ", fishOrder.Select(f => f + "=" + SeedsNow(f, window, frac, weather)).ToArray()));
        }
        catch (Exception e)
        {
            CombatLog.Write("[Fishing] (error) " + e.Message);
        }
    }

    // ---------- Water lookup ----------
    public static FishableWater FindWater(Vector3 p)
    {
        var hits = Physics.OverlapSphere(p, 1f, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < hits.Length; i++)
        {
            var w = hits[i].GetComponentInParent<FishableWater>();
            if (w != null) return w;
        }
        FishableWater best = null; float bestD = float.MaxValue;
        var all = UnityEngine.Object.FindObjectsOfType<FishableWater>();
        for (int i = 0; i < all.Length; i++)
        {
            var w = all[i];
            var col = w.GetComponent<Collider>();
            if (col == null) col = w.GetComponentInChildren<Collider>();
            float d = col != null ? col.bounds.SqrDistance(p) : (w.transform.position - p).sqrMagnitude;
            if (d < bestD) { bestD = d; best = w; }
        }
        return best;
    }

    // ---------- Clock / weather ----------
    static void Clock(out int window, out double frac)
    {
        long ms;
        var nm = OrbusNetManager.Instance;
        double st = nm != null ? nm.currentServerTime() : 0;
        if (st > 1e12) ms = (long)st;
        else ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        window = (int)((ms % 86400000L) / 18000000L);
        frac = (ms % 18000000L) / 18000000.0;
    }

    public static int CurrentWeather()
    {
        var nm = OrbusNetManager.Instance;
        if (nm == null) return -1;
        return (byte)weatherField.GetValue(nm);
    }

    public static string WeatherName(int w)
    {
        switch (w)
        {
            case 0: return "CLEAR";
            case 1: return "CLOUDY";
            case 2: return "RAIN";
            case 3: return "THUNDERING";
            case 6: return "UNDERGROUND";
            case 7: return "INDOORS";
            case -1: return "UNKNOWN";
            default: return "STATE" + w;
        }
    }

    public static string WeatherDisplayName(int w)
    {
        switch (w)
        {
            case 0: return "Clear";
            case 1: return "Cloudy";
            case 2: return "Rain";
            case 3: return "Thundering";
            case 6: return "Underground";
            case 7: return "Indoors";
            case -1: return "Unknown";
            default: return "State" + w;
        }
    }

    static string WeatherList(int[] w)
    {
        return string.Join("/", w.Select(x => WeatherName(x)).ToArray());
    }

    // ---------- Fish logic ----------
    public static int SlotOf(string name)
    {
        int sum = 0; foreach (char c in name) sum += c; return sum % 5;
    }

    static bool TimeOk(Scn s, double frac)
    {
        if (s.time == null) return true;
        return s.time == "late" ? frac > 0.75 : frac < 0.25;
    }

    static bool WeatherOk(Scn s, int weather)
    {
        return s.weather == null || Array.IndexOf(s.weather, weather) >= 0;
    }

    static string SeedsNow(string fish, int window, double frac, int weather)
    {
        var seeds = new List<int>();
        foreach (var s in scenarios)
            if (s.fish == fish && s.window == window && TimeOk(s, frac) && WeatherOk(s, weather) && !seeds.Contains(s.seed))
                seeds.Add(s.seed);
        seeds.Sort();
        return seeds.Count == 0 ? "none" : string.Join(",", seeds.Select(x => x.ToString()).ToArray());
    }

    static string Evaluate(string fish, int seed, int window, double frac, int weather)
    {
        int bestMet = -1, bestTotal = 0;
        List<string> bestMissing = null;

        foreach (var s in scenarios)
        {
            if (s.fish != fish) continue;
            int met = 0, total = 0;
            var missing = new List<string>();

            total++;
            if (s.seed == seed) met++;
            else missing.Add("WRONG LAKE seed (need " + s.seed + ", this lake is " + seed + ")");

            total++;
            if (s.window == window) met++; else missing.Add("window " + s.window + " (now " + window + ")");

            if (s.time != null)
            {
                total++;
                if (TimeOk(s, frac)) met++;
                else missing.Add("time " + (s.time == "late" ? ">0.75" : "<0.25") + " (now " + frac.ToString("F2") + ")");
            }
            if (s.weather != null)
            {
                total++;
                if (WeatherOk(s, weather)) met++;
                else missing.Add("weather " + WeatherList(s.weather) + " (now " + WeatherName(weather) + ")");
            }

            if (met > bestMet) { bestMet = met; bestTotal = total; bestMissing = missing; }
        }

        string head = fish + "(" + bestMet + "/" + bestTotal + ")";
        if (bestMissing.Count == 0) return head + " ACTIVE";
        return head + " MISSING: " + string.Join(", ", bestMissing.ToArray());
    }
}


// ---------- Fishing location / weather change popup ----------
// Uses the fishing pole's alert text as a small summary display.
// Fires only when the seed (water) or the weather differs from the last
// cast, so repeat casts in the same water with the same weather are silent.

[HarmonyPatch(typeof(Fisher), "FixedUpdate")]
class FishLocationPopupPatch
{
    // What we saw last cast. -1 / -2 are sentinels so the first cast
    // of a session always fires.
    static int lastSeed = -1;
    static int lastWeather = -2;

    static readonly FieldInfo stateField =
        AccessTools.Field(typeof(Fisher), "currentState");

    static readonly FieldInfo bobberField =
        AccessTools.Field(typeof(Fisher), "currentBobber");

    static readonly MethodInfo alertTextField =
        AccessTools.Method(typeof(Fisher), "setAlertText");

    static void Postfix(Fisher __instance)
    {
        if (!CombatLogger.ShowFishing) return;
        if (!__instance.isLocallyControlled) return;

        try
        {
            // currentState is private; read it via reflection.
            // 4 == Fisher.FishingPoleState.BOBBING (see decompile).
            object stateObj = stateField.GetValue(__instance);
            if (stateObj == null || Convert.ToInt32(stateObj) != 4)
                return;

            var bobber = (GameObject)bobberField.GetValue(__instance);
            Vector3 p = bobber != null ? bobber.transform.position : __instance.transform.position;

            var water = FishWaterLogPatch.FindWater(p);
            if (water == null) return;

            int seed = FishWaterLogPatch.SlotOf(water.gameObject.name);
            int weather = FishWaterLogPatch.CurrentWeather();

            bool seedChanged = seed != lastSeed;
            bool weatherChanged = weather != lastWeather;

            if (!seedChanged && !weatherChanged)
                return;

            lastSeed = seed;
            lastWeather = weather;

            string line = water.gameObject.name +
                          "\nSeed " + seed +
                          "\n[" + water.fishTable + "]" +
                          "\n" + FishWaterLogPatch.WeatherDisplayName(weather);

            // setAlertText is private; invoke via the cached MethodInfo.
            alertTextField.Invoke(__instance, new object[] { line });
        }
        catch (Exception e)
        {
            CombatLog.Write("[Fishing] (popup error) " + e.Message);
        }
    }
}