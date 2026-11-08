using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CS2PerformanceManager;

internal sealed class GameMonitor
{
    private const uint SetInformation = 0x0200;
    private const uint QueryLimitedInformation = 0x1000;
    private readonly Dictionary<int, Attempt> attempts = new();

    private int revision;

    public string Scan(ProcessPriorityClass priority, ulong affinity)
    {
        var activeIds = new HashSet<int>();
        var statuses = new List<string>();
        foreach (Process process in Process.GetProcessesByName("cs2"))
        {
            using (process)
            {
                int pid = process.Id;
                activeIds.Add(pid);
                try
                {
                    statuses.Add(Apply(pid, priority, affinity));
                }
                catch (Win32Exception ex)
                {
                    statuses.Add($"PID {pid} · {ex.Message} (kod {ex.NativeErrorCode})");
                }
            }
        }

        foreach (int pid in attempts.Keys.Where(pid => !activeIds.Contains(pid)).ToArray())
            attempts.Remove(pid);

        return statuses.Count == 0 ? "Oczekiwanie na cs2.exe…" : string.Join(Environment.NewLine, statuses);
    }

    public void Retry() => revision++;

    private string Apply(int pid, ProcessPriorityClass priority, ulong affinity)
    {
        using SafeProcessHandle handle = OpenProcess(SetInformation | QueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
            throw new Win32Exception();

        var path = new StringBuilder(32768);
        int size = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref size))
            throw new Win32Exception();

        // The image check and both writes use the same handle, even if a PID is recycled.
        if (!string.Equals(Path.GetFileName(path.ToString()), "cs2.exe", StringComparison.OrdinalIgnoreCase))
            return $"PID {pid} · pominięto po sprawdzeniu nazwy pliku.";

        if (!GetProcessTimes(handle, out long created, out _, out _, out _))
            throw new Win32Exception();

        long now = Environment.TickCount64;
        if (!attempts.TryGetValue(pid, out Attempt previous) || previous.Created != created)
            previous = default;

        if (previous.Created == created && previous.Revision == revision &&
            previous.Priority == priority && previous.Affinity == affinity && now < previous.RetryAfter)
            return previous.Status;

        ulong originalMask = previous.OriginalMask;
        ulong lastApplied = previous.LastApplied;
        int affinityError = 0;
        if (affinity != 0 || lastApplied != 0)
        {
            if (!GetProcessAffinityMask(handle, out nuint current, out _))
            {
                affinityError = Marshal.GetLastWin32Error();
            }
            else if (affinity != 0)
            {
                if (SetProcessAffinityMask(handle, (nuint)affinity))
                {
                    if (lastApplied == 0 || (ulong)current != lastApplied)
                        originalMask = (ulong)current;
                    lastApplied = affinity;
                }
                else
                {
                    affinityError = Marshal.GetLastWin32Error();
                }
            }
            else if ((ulong)current != lastApplied || SetProcessAffinityMask(handle, (nuint)originalMask))
            {
                lastApplied = 0;
            }
            else
            {
                affinityError = Marshal.GetLastWin32Error();
            }
        }

        int priorityError = 0;
        if (!SetPriorityClass(handle, (uint)priority))
            priorityError = Marshal.GetLastWin32Error();

        string affinityStatus = affinity == 0 && affinityError == 0 ? "affinity bez zmian" :
            Describe("affinity", $"0x{affinity:X}", affinityError);
        string priorityStatus = Describe("priorytet", Preferences.Label(priority), priorityError);
        string status = $"PID {pid} · {affinityStatus} · {priorityStatus}";
        long retryAfter = affinityError == 0 && priorityError == 0 ? long.MaxValue : now + 10_000;
        attempts[pid] = new Attempt(created, priority, affinity, revision, originalMask, lastApplied, retryAfter, status);
        return status;
    }

    private static string Describe(string label, string value, int error) =>
        error == 0 ? $"{label} {value}" :
            $"{label}: {new Win32Exception(error).Message} (kod {error})";

    private readonly record struct Attempt(long Created, ProcessPriorityClass Priority, ulong Affinity, int Revision,
        ulong OriginalMask, ulong LastApplied, long RetryAfter, string Status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
        out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(SafeProcessHandle process, out nuint processMask, out nuint systemMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessAffinityMask(SafeProcessHandle process, nuint mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);
}

