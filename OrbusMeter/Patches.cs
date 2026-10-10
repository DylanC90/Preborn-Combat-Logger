using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using MagicalActual;

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


// ---------- Damage component: Monster method ID 6 ----------

[HarmonyPatch]
class ComponentListenerPatch
{
    static Type listenerType;
    static Func<object, byte> getMethodId;
    static Func<object, List<string>> getTypes;

    static List<string> validatedTypes;

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

    static void Prefix(object __instance, BinaryReader reader)
    {
        try
        {
            //Nearly every doRead exits here after one compiled field read.
            if (getMethodId(__instance) != 6)
                return;

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