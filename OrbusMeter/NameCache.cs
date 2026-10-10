using System;
using MagicalActual;

// ---------- Name cache ----------
//
// guid is a ushort, so a flat array beats any dictionary.
// Reference reads/writes are atomic, so no locks are needed.

public static class NameCache
{
    const int RETRY_MS = 500;

    static readonly string[] names = new string[65536];     // display name
    static readonly string[] rawNames = new string[65536];  // last monsterName seen
    static readonly long[] retryAt = new long[65536];       // negative-cache deadline (ticks)

    // Returns null when unresolved. The writer formats that as Unknown(guid),
    // so no string is allocated on the game thread for failures.
    public static string Resolve(ushort guid, long nowTicks)
    {
        if (guid == 0)
            return null;

        string cached = names[guid];
        if (cached != null)
            return cached;

        // Negative cache: don't redo the expensive lookup for every hit.
        if (nowTicks < retryAt[guid])
            return null;

        retryAt[guid] = nowTicks + RETRY_MS * TimeSpan.TicksPerMillisecond;

        try
        {
            var manager = OrbusNetManager.Instance;
            if (manager != null)
            {
                var entity = manager.findEntityByGuid(guid);
                if (entity != null && entity.baseObject != null)
                {
                    var monster = entity.baseObject.GetComponent<Monster>();
                    if (monster != null)
                    {
                        string raw = monster.monsterName;
                        if (!string.IsNullOrEmpty(raw))
                        {
                            string name = monster.isPlayer ? raw : raw + "(" + guid + ")";
                            names[guid] = name;
                            rawNames[guid] = raw;
                            return name;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    // Called from the UpdateHealthBar postfix, which fires very often.
    // Steady state is two array reads and a reference compare: no lock, no alloc.
    public static void CacheMonsterName(Monster monster)
    {
        try
        {
            if (monster == null)
                return;

            string raw = monster.monsterName;
            if (string.IsNullOrEmpty(raw))
                return;

            var entity = monster.myEntity;
            if (entity == null)
                return;

            ushort guid = entity.guid;

            string prev = rawNames[guid];
            if (prev != null && (ReferenceEquals(prev, raw) || string.Equals(prev, raw)))
                return;

            names[guid] = monster.isPlayer ? raw : raw + "(" + guid + ")";
            rawNames[guid] = raw;
        }
        catch
        {
        }
    }
}