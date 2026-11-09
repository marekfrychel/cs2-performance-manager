using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CS2PerformanceManager;

internal static class CpuTopology
{
    private const int ProcessorCore = 0;
    private const int Cache = 2;
    private const int InsufficientBuffer = 122;

    public static CpuLayout Detect()
    {
        string cpu = "Procesor";
        ulong[] cores = Array.Empty<ulong>();
        try
        {
            cpu = (Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", "") as string ?? "").Trim();
            if (cpu.Length == 0)
                cpu = "Procesor";

            ulong active = AffinitySelection.ActiveMask;
            if (IntPtr.Size != 8 || active == 0)
                throw new InvalidOperationException("Wybór wątków wymaga jednej grupy procesorów.");

            ulong[] detected = ReadMasks(ProcessorCore);
            ulong covered = 0;
            foreach (ulong core in detected)
            {
                if (core == 0 || (covered & core) != 0)
                    throw new InvalidOperationException("Windows zwrócił niespójną topologię rdzeni.");
                covered |= core;
            }

            if (covered != active)
                throw new InvalidOperationException("Nie udało się dopasować rdzeni do aktywnych wątków.");

            cores = detected.OrderBy(core => BitOperations.TrailingZeroCount(core)).ToArray();
            bool namedCcds = Regex.IsMatch(cpu, @"\bAMD Ryzen [59] (5600X|5900X)\b", RegexOptions.IgnoreCase);
            return new CpuLayout(cpu, cores, OrderGroups(cores, ReadMasks(Cache)), "", namedCcds);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or SecurityException)
        {
            return new CpuLayout(cpu, cores, Array.Empty<ulong>(), ex.Message);
        }
    }

    internal static ulong[] OrderGroups(ulong[] cores, ulong[] caches)
    {
        ulong covered = 0;
        ulong allCores = cores.Aggregate(0UL, (mask, core) => mask | core);
        foreach (ulong cache in caches.Distinct())
        {
            if (cache == 0 || (covered & cache) != 0 || (cache & ~allCores) != 0 ||
                cores.Any(core => (core & cache) != 0 && (core & cache) != core))
                throw new InvalidOperationException("Nie udało się przypisać pełnych rdzeni do grup L3.");
            covered |= cache;
        }

        if (covered != allCores)
            throw new InvalidOperationException("Grupy L3 nie obejmują wszystkich rdzeni.");

        return caches.Distinct().OrderBy(mask => BitOperations.TrailingZeroCount(mask)).ToArray();
    }

    private static ulong[] ReadMasks(int relationship)
    {
        uint length = 0;
        if (GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref length) ||
            Marshal.GetLastWin32Error() != InsufficientBuffer || length == 0)
            throw new Win32Exception();

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(relationship, buffer, ref length))
                throw new Win32Exception();

            var masks = new List<ulong>();
            for (int offset = 0; offset < length;)
            {
                if (length - offset < 8)
                    throw new InvalidOperationException("niepełny nagłówek topologii");

                IntPtr record = IntPtr.Add(buffer, offset);
                int size = Marshal.ReadInt32(record, 4);
                if (size < 8 || size > length - offset || Marshal.ReadInt32(record) != relationship)
                    throw new InvalidOperationException("nieprawidłowy rekord topologii");

                if (relationship == ProcessorCore ||
                    (size >= 20 && Marshal.ReadByte(record, 8) == 3 &&
                     Marshal.ReadInt32(record, 16) == 0)) // Unified L3
                {
                    // x64 Win32 layout. Older Windows cache records reserve the group count field.
                    int affinityOffset = relationship == ProcessorCore ? 32 : 40;
                    if (size != affinityOffset + 16 || Marshal.ReadInt16(record, affinityOffset + 8) != 0)
                        throw new InvalidOperationException("nieobsługiwana grupa procesorów");

                    short groupCount = Marshal.ReadInt16(record, affinityOffset - 2);
                    if (groupCount != 1 && !(relationship == Cache && groupCount == 0))
                        throw new InvalidOperationException("nieobsługiwana liczba grup w rekordzie topologii");

                    masks.Add(unchecked((ulong)Marshal.ReadInt64(record, affinityOffset)));
                }

                offset += size;
            }

            return masks.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}



internal sealed record CpuLayout(string Name, ulong[] Cores, ulong[] Groups, string Note, bool NamedCcds = false)
{
    public string GroupName(int index) => NamedCcds ? $"CCD{index}" : $"Grupa L3 {index + 1}";

    public ulong OneThreadPerCore(ulong domain = ulong.MaxValue) => Cores
        .Where(core => (core & domain) == core)
        .Aggregate(0UL, (mask, core) => mask | (1UL << BitOperations.TrailingZeroCount(core)));

    public ulong ProfileMask(AffinityMode mode)
    {
        int index = mode switch
        {
            AffinityMode.Ccd0 or AffinityMode.Ccd0Smt => 0,
            AffinityMode.Ccd1 or AffinityMode.Ccd1Smt => 1,
            _ => -1
        };
        if (index < 0 || index >= Groups.Length)
            return 0;

        ulong domain = Groups[index];
        ulong singleThreads = OneThreadPerCore(domain);
        return mode is AffinityMode.Ccd0Smt or AffinityMode.Ccd1Smt
            ? (domain != singleThreads ? domain : 0)
            : singleThreads;
    }
}
