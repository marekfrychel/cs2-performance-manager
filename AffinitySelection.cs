using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CS2PerformanceManager;

internal enum AffinityMode { Ccd1, Custom, Unchanged, Ccd0, Ccd0Smt, Ccd1Smt }

internal static class AffinitySelection
{
    public static ulong ActiveMask { get; } =
        GetActiveProcessorGroupCount() == 1 &&
        GetProcessAffinityMask(GetCurrentProcess(), out _, out nuint active)
            ? (ulong)active : 0;

    public static bool TryParse(string input, ulong active, out ulong mask)
    {
        mask = 0;
        if (active == 0)
            return false;

        foreach (string part in input.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int cpu) ||
                cpu is < 0 or > 63 || (active & (1UL << cpu)) == 0)
                return false;
            mask |= 1UL << cpu;
        }

        return mask != 0;
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out nuint processMask, out nuint systemMask);
}

