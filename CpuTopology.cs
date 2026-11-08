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

    public static (ulong Mask, string Description) Detect()
    {
        try
        {
            string cpu = (Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", "") as string ?? "").Trim();
            if (!Regex.IsMatch(cpu, @"\bAMD Ryzen 9 5900X\b", RegexOptions.IgnoreCase))
                return (0, $"Profil CCD1 wymaga Ryzen 9 5900X. Wykryto: {(cpu.Length == 0 ? "nieznany CPU" : cpu)}.");

            if (IntPtr.Size != 8 || GetActiveProcessorGroupCount() != 1)
                throw new InvalidOperationException("wymagany Windows x64 z jedną grupą procesorów");

            ulong[] cores = ReadMasks(ProcessorCore);
            ulong[] l3 = ReadMasks(Cache);
            ulong mask = SelectCcd1(cores, l3);
            string logicalCpus = string.Join(", ", Enumerable.Range(0, 64)
                .Where(cpuIndex => (mask & (1UL << cpuIndex)) != 0));
            return (mask, $"CCD1 bez SMT · CPU {logicalCpus} · 0x{mask:X}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or SecurityException)
        {
            return (0, $"Pominięto affinity: {ex.Message}.");
        }
    }

    internal static ulong SelectCcd1(ulong[] cores, ulong[] l3)
    {
        const string unexpected = "oczekiwano 12 rdzeni w dwóch rozłącznych domenach L3 po 6 rdzeni";
        if (cores.Length != 12 || l3.Length != 2 || l3.Any(mask => mask == 0) ||
            (l3[0] & l3[1]) != 0)
            throw new InvalidOperationException(unexpected);

        int threadsPerCore = BitOperations.PopCount(cores[0]);
        if (threadsPerCore is < 1 or > 2)
            throw new InvalidOperationException(unexpected);

        // Name the domain with the higher first Windows logical CPU number CCD1.
        int second = BitOperations.TrailingZeroCount(l3[0]) < BitOperations.TrailingZeroCount(l3[1]) ? 1 : 0;
        int[] coreCounts = new int[2];
        ulong allCores = 0;
        ulong selected = 0;
        foreach (ulong core in cores)
        {
            if (BitOperations.PopCount(core) != threadsPerCore || (allCores & core) != 0)
                throw new InvalidOperationException(unexpected);

            int domain = (core & l3[0]) == core ? 0 : 1;
            if ((core & l3[domain]) != core)
                throw new InvalidOperationException(unexpected);

            coreCounts[domain]++;
            allCores |= core;
            if (domain == second)
                selected |= 1UL << BitOperations.TrailingZeroCount(core);
        }

        if (coreCounts[0] != 6 || coreCounts[1] != 6 || allCores != (l3[0] | l3[1]))
            throw new InvalidOperationException(unexpected);
        return selected;
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

