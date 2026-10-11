using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using MagicalActual;

// ---------- Writer: queue, background thread, zero-alloc formatter ----------

public static class CombatLogWriter
{
    const byte ENTRY_COMBAT = 0;
    const byte ENTRY_COMBAT_CRIT = 1;
    const byte ENTRY_TEXT = 2;
    const byte ENTRY_LOOT = 3;

    struct LogEvent
    {
        public long TimestampTicks;// UTC ticks at capture time
        public string TargetName;// null = unresolved
        public string AttackerName;// null = unresolved
        public string Text;// body for system/error lines
        public int Damage;
        public ushort TargetGuid;
        public ushort AttackerGuid;
        public byte EntryKind;// ENTRY_COMBAT, ENTRY_COMBAT_CRIT or ENTRY_TEXT
    }

    const int WRITER_INTERVAL_MS = 200;
    const int MAX_PENDING = 100000;

    static StreamWriter writer;
    static Thread writerThread;
    static volatile bool running;
    static volatile bool stopping;

    // Two lists swapped under a lock: the game thread adds to incomingEvents,
    // the writer thread swaps in spareEvents and writes out what was collected.
    static readonly object queueLock = new object();
    static List<LogEvent> incomingEvents = new List<LogEvent>(1024);
    static List<LogEvent> spareEvents = new List<LogEvent>(1024);

    // ---------- File lifecycle ----------

    public static void Open(string path)
    {
        var fs = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite,
            4096,
            FileOptions.SequentialScan);

        writer = new StreamWriter(fs, new UTF8Encoding(false), 32768);
        writer.AutoFlush = false;
    }

    public static void Start()
    {
        running = true;

        writerThread = new Thread(WriterLoop);
        writerThread.IsBackground = true;
        writerThread.Name = "CombatLogWriter";
        writerThread.Priority = System.Threading.ThreadPriority.BelowNormal;
        writerThread.Start();
    }

    public static void Stop()
    {
        stopping = true;

        try
        {
            if (writerThread != null)
                writerThread.Join(3000);
        }
        catch
        {
        }

        running = false;
    }

    // ---------- Enqueue (called on the game thread) ----------
    public static void WriteLine(string body)
    {
        if (!running)
            return;

        lock (queueLock)
        {
            if (incomingEvents.Count >= MAX_PENDING)
                return;

            incomingEvents.Add(new LogEvent
            {
                TimestampTicks = DateTime.UtcNow.Ticks,
                Text = body,
                EntryKind = ENTRY_TEXT
            });
        }
    }

    public static void EnqueueCombat(long ticks, string target, ushort targetGuid,
                                     string attacker, ushort attackerGuid,
                                     int damage, bool crit)
    {
        if (!running)
            return;

        lock (queueLock)
        {
            if (incomingEvents.Count >= MAX_PENDING)
                return; // drop rather than grow without bound

            incomingEvents.Add(new LogEvent
            {
                TimestampTicks = ticks,
                TargetName = target,
                AttackerName = attacker,
                TargetGuid = targetGuid,
                AttackerGuid = attackerGuid,
                Damage = damage,
                EntryKind = crit ? ENTRY_COMBAT_CRIT : ENTRY_COMBAT
            });
        }
    }

    public static void EnqueueLoot(long ticks, int amount)
    {
        if (!running)
            return;

        lock (queueLock)
        {
            if (incomingEvents.Count >= MAX_PENDING)
                return;

            incomingEvents.Add(new LogEvent
            {
                TimestampTicks = ticks,
                Damage = amount, // reusing Damage for the XP amount
                EntryKind = ENTRY_LOOT
            });
        }
    }

    static long utcOffsetTicks;
    static int lastOffsetRefreshTick;

    // Reusable line buffer: each log line is built here, then written to the file.
    static char[] lineBuffer = new char[1024];
    static int lineLength;

    static void WriterLoop()
    {
        RefreshUtcOffset();

        while (true)
        {
            //Read the flag first so anything queued before shutdown still gets written.
            bool shouldStopAfterThisPass = stopping;

            try
            {
                if (unchecked(Environment.TickCount - lastOffsetRefreshTick) > 60000)
                    RefreshUtcOffset();

                List<LogEvent> eventsToWrite;
                lock (queueLock)
                {
                    eventsToWrite = incomingEvents;
                    incomingEvents = spareEvents;
                    spareEvents = eventsToWrite;
                }

                int eventCount = eventsToWrite.Count;
                if (eventCount > 0)
                {
                    for (int index = 0; index < eventCount; index++)
                    {
                        LogEvent logEvent = eventsToWrite[index];
                        FormatLine(ref logEvent);
                        writer.Write(lineBuffer, 0, lineLength);
                    }

                    eventsToWrite.Clear();
                    writer.Flush();
                }
            }
            catch
            {
                // do nothing
            }

            if (shouldStopAfterThisPass)
                break;

            Thread.Sleep(WRITER_INTERVAL_MS);
        }

        try
        {
            writer.Flush();
            writer.Dispose();
        }
        catch
        {
        }

        writer = null;
    }

    //Local time zone offset, needed to turn UTC ticks into local clock time.
    static void RefreshUtcOffset()
    {
        try
        {
            utcOffsetTicks = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).Ticks;
        }
        catch
        {
        }

        lastOffsetRefreshTick = Environment.TickCount;
    }

    //Formats one line into the reusable buffer with no allocations.
    static void FormatLine(ref LogEvent logEvent)
    {
        lineLength = 0;

        // HH:mm:ss:fff straight from ticks. Much faster than using DateTime.
        long localTicks = logEvent.TimestampTicks + utcOffsetTicks;
        long ticksIntoDay = localTicks % TimeSpan.TicksPerDay;
        if (ticksIntoDay < 0) ticksIntoDay += TimeSpan.TicksPerDay;

        AppendPaddedDigits((int)(ticksIntoDay / TimeSpan.TicksPerHour), 2);
        AppendChar(':');
        AppendPaddedDigits((int)(ticksIntoDay / TimeSpan.TicksPerMinute % 60), 2);
        AppendChar(':');
        AppendPaddedDigits((int)(ticksIntoDay / TimeSpan.TicksPerSecond % 60), 2);
        AppendChar(':');
        AppendPaddedDigits((int)(ticksIntoDay / TimeSpan.TicksPerMillisecond % 1000), 3);

        if (logEvent.EntryKind == ENTRY_TEXT)
        {
            AppendChar(' ');
            AppendString(logEvent.Text);
        }
        else if (logEvent.EntryKind == ENTRY_LOOT)
        {
            AppendString(" [Loot] Gained ");
            AppendInt(logEvent.Damage);
            AppendString(" XP");
        }
        else
        {
            AppendString(" [Combat] ");
            AppendName(logEvent.TargetName, logEvent.TargetGuid);
            AppendString(" took ");
            AppendInt(logEvent.Damage);
            AppendString(" damage from ");
            AppendName(logEvent.AttackerName, logEvent.AttackerGuid);

            if (logEvent.EntryKind == ENTRY_COMBAT_CRIT)
                AppendString(" (Critical)");
        }

        AppendString(Environment.NewLine);
    }

    static void AppendName(string name, ushort guid)
    {
        if (name != null)
        {
            AppendString(name);
            return;
        }

        AppendString("Unknown(");
        AppendInt(guid);
        AppendChar(')');
    }

    //============================================================================================================
    // Buffer helpers
    //
    // Optimization: a hand-rolled char[] buffer instead of String.Concat,
    // because the log path runs hundreds of times per second during combat and every
    // + / int.ToString() would allocate on the heap, feeding gen0 GC and causing
    // frame hitches on a latency-critical game thread.

    // Makes sure the buffer has room for 'extraChars' more characters, doubling it if not.
    static void EnsureCapacity(int extraChars)
    {
        if (lineLength + extraChars <= lineBuffer.Length)
            return;

        int newSize = lineBuffer.Length * 2;
        while (newSize < lineLength + extraChars)
            newSize *= 2;

        Array.Resize(ref lineBuffer, newSize);
    }

    static void AppendChar(char character)
    {
        EnsureCapacity(1);
        lineBuffer[lineLength++] = character;
    }

    static void AppendString(string text)
    {
        if (text == null)
            return;

        EnsureCapacity(text.Length);
        text.CopyTo(0, lineBuffer, lineLength, text.Length);
        lineLength += text.Length;
    }
    static void AppendPaddedDigits(int value, int width)
    {
        EnsureCapacity(width);
        for (int position = width - 1; position >= 0; position--)
        {
            lineBuffer[lineLength + position] = (char)('0' + value % 10);
            value /= 10;
        }
        lineLength += width;
    }

    // Writes an int without calling ToString(), so nothing is allocated.
    static void AppendInt(int value)
    {
        EnsureCapacity(12);

        long remaining = value;
        if (remaining < 0)
        {
            lineBuffer[lineLength++] = '-';
            remaining = -remaining;
        }

        int digitCount = 1;
        for (long counter = remaining; counter >= 10; counter /= 10)
            digitCount++;

        for (int position = digitCount - 1; position >= 0; position--)
        {
            lineBuffer[lineLength + position] = (char)('0' + (int)(remaining % 10));
            remaining /= 10;
        }
        lineLength += digitCount;
    }
}