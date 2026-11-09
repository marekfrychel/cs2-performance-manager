using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CS2PerformanceManager;

internal sealed record Preferences(ProcessPriorityClass Priority, bool StartWithWindows = false,
    AffinityMode Affinity = AffinityMode.Ccd1, string CustomCpus = "")
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "CS2PerformanceManager";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CS2PerformanceManager", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string Label(ProcessPriorityClass priority) =>
        priority == ProcessPriorityClass.AboveNormal ? "Above Normal" : priority.ToString();

    public static Preferences Load()
    {
        if (!File.Exists(SettingsPath))
            return new Preferences(ProcessPriorityClass.Normal, Affinity: AffinityMode.Unchanged);

        var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath), JsonOptions);
        if (settings?.Priority is not (ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal or ProcessPriorityClass.High))
            throw new JsonException("Wybierz Normal, Above Normal lub High i zapisz ustawienia.");
        if (!Enum.IsDefined(settings.Affinity))
            throw new JsonException("Nieprawidłowy tryb wyboru wątków.");
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static bool StartsWithWindows()
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(RunName) is string;
    }

    public static void SetStartup(bool enabled)
    {
        using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Nie można ustalić ścieżki do EXE.");
            if (!string.Equals(Path.GetFileName(executable), "CS2PerformanceManager.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Otwórz CS2PerformanceManager.exe, aby włączyć autostart.");

            string command = $"\"{executable}\" --tray";
            if (command.Length > 260)
                throw new InvalidOperationException("Przenieś aplikację do krótszej ścieżki, aby włączyć autostart.");

            run.SetValue(RunName, command, RegistryValueKind.String);
        }
        else
        {
            run.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }
}



