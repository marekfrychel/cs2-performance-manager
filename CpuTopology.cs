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
            if (!Regex.IsMatch(cpu, @"\bAMD Ryzen 9 5900X\b", RegexOptions.IgnoreCase))
                return new CpuLayout(cpu, cores, Array.Empty<ulong>(), "Profile CCD dostępne dla Ryzen 9 5900X.");

            return new CpuLayout(cpu, cores, OrderCcds(cores, ReadMasks(Cache)), "");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or SecurityException)
        {
            return new CpuLayout(cpu, cores, Array.Empty<ulong>(), ex.Message);
        }
    }

    internal static ulong[] OrderCcds(ulong[] cores, ulong[] l3)
    {
        const string unexpected = "Profile CCD wymagają dwóch domen L3 po sześć pełnych rdzeni.";
        if (cores.Length != 12 || l3.Length != 2 || (l3[0] & l3[1]) != 0)
            throw new InvalidOperationException(unexpected);

        int threadsPerCore = BitOperations.PopCount(cores[0]);
        if (threadsPerCore is < 1 or > 2)
            throw new InvalidOperationException(unexpected);

        ulong covered = 0;
        foreach (ulong core in cores)
        {
            if (BitOperations.PopCount(core) != threadsPerCore || (covered & core) != 0 ||
                ((core & l3[0]) != core && (core & l3[1]) != core))
                throw new InvalidOperationException(unexpected);
            covered |= core;
        }

        if (covered != (l3[0] | l3[1]) || l3.Any(mask => BitOperations.PopCount(mask) != 6 * threadsPerCore))
            throw new InvalidOperationException(unexpected);

        return l3.OrderBy(mask => BitOperations.TrailingZeroCount(mask)).ToArray();
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
                     Marshal.ReadInt32(record, 12) == 32 * 1024 * 1024 &&
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

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}



internal sealed record CpuLayout(string Name, ulong[] Cores, ulong[] Ccds, string Note)
{
    public bool HasSmt => Cores.Any(core => BitOperations.PopCount(core) > 1);

    public ulong ProfileMask(AffinityMode mode)
    {
        if (Ccds.Length != 2)
            return 0;

        ulong domain = mode is AffinityMode.Ccd0 or AffinityMode.Ccd0Smt ? Ccds[0] : Ccds[1];
        if (mode is AffinityMode.Ccd0Smt or AffinityMode.Ccd1Smt)
            return HasSmt ? domain : 0;

        ulong mask = 0;
        foreach (ulong core in Cores.Where(core => (core & domain) == core))
            mask |= 1UL << BitOperations.TrailingZeroCount(core);
        return mask;
    }
}
