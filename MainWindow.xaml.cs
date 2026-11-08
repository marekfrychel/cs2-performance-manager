using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace CS2PerformanceManager;

public partial class MainWindow : Window
{
    private readonly GameMonitor monitor;
    private readonly DispatcherTimer timer;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip trayMenu;
    private Preferences preferences = new(ProcessPriorityClass.Normal);
    private bool exiting;
    private readonly CpuLayout topology;
    private readonly Dictionary<int, ToggleButton> threadButtons = new();
    private Dictionary<AffinityMode, RadioButton> profiles = null!;
    private AffinityMode selectedMode;
    private readonly System.Drawing.Icon trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            preferences = Preferences.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Feedback.Text = $"Użyto Normal. Błąd odczytu ustawień: {ex.Message}";
        }

        topology = CpuTopology.Detect();
        BuildCpuMap();

        profiles = new()
        {
            [AffinityMode.Ccd0] = Ccd0NoSmt,
            [AffinityMode.Ccd0Smt] = Ccd0WithSmt,
            [AffinityMode.Ccd1] = Ccd1NoSmt,
            [AffinityMode.Ccd1Smt] = Ccd1WithSmt,
            [AffinityMode.Custom] = CustomOption,
            [AffinityMode.Unchanged] = UnchangedOption
        };
        foreach (var (mode, button) in profiles)
        {
            button.IsEnabled = mode switch
            {
                AffinityMode.Unchanged => true,
                AffinityMode.Custom => threadButtons.Count != 0,
                _ => topology.ProfileMask(mode) != 0
            };
            AutomationProperties.SetName(button, mode switch
            {
                AffinityMode.Ccd0 => "CCD0 bez SMT",
                AffinityMode.Ccd0Smt => "CCD0 z SMT",
                AffinityMode.Ccd1 => "CCD1 bez SMT",
                AffinityMode.Ccd1Smt => "CCD1 z SMT",
                _ => button.Content.ToString()
            });
        }

        RadioButton initial = profiles[preferences.Affinity];
        if (initial.IsEnabled)
        {
            initial.IsChecked = true;
            if (preferences.Affinity == AffinityMode.Custom)
                SetThreadSelection(ResolveMask(preferences));
        }
        else
        {
            UnchangedOption.IsChecked = true;
            Feedback.Text = "Zapisany profil jest niedostępny na tym procesorze.";
        }

        NormalPriority.IsChecked = preferences.Priority == ProcessPriorityClass.Normal;
        AbovePriority.IsChecked = preferences.Priority == ProcessPriorityClass.AboveNormal;
        HighPriority.IsChecked = preferences.Priority == ProcessPriorityClass.High;

        try
        {
            StartupOption.IsChecked = Preferences.StartsWithWindows();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            StartupOption.IsChecked = preferences.StartWithWindows;
            StartupOption.IsEnabled = false;
            Feedback.Text += $" Nie można odczytać autostartu: {ex.Message}";
        }

        monitor = new GameMonitor();
        var iconResource = Application.GetResourceStream(new Uri("App.ico", UriKind.Relative));
        using (var stream = iconResource.Stream)
        using (var icon = new System.Drawing.Icon(stream))
            trayIcon = (System.Drawing.Icon)icon.Clone();

        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Otwórz", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Zakończ", null, (_, _) => Quit());
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "CS2 Performance Manager",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowFromTray();

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Scan();
        timer.Start();
        Scan();
    }

    private void Scan()
    {
        try
        {
            ulong mask = ResolveMask(preferences);
            GameStatus.Text = monitor.Scan(preferences.Priority, mask);
            if (mask == 0 && preferences.Affinity == AffinityMode.Custom)
                GameStatus.Text += " Zapisana lista CPU nie pasuje do tego komputera.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            GameStatus.Text = $"Błąd wykrywania cs2.exe: {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var priority = ProcessPriorityClass.Normal;
        if (HighPriority.IsChecked == true)
            priority = ProcessPriorityClass.High;
        else if (AbovePriority.IsChecked == true)
            priority = ProcessPriorityClass.AboveNormal;

        ulong mask = SelectedMask();
        if (selectedMode == AffinityMode.Custom && mask == 0)
        {
            Feedback.Text = "Zaznacz co najmniej jeden wątek albo wybierz „Bez zmian”.";
            return;
        }

        string customCpus = string.Join(", ", CpuNumbers(mask));
        try
        {
            var updated = new Preferences(priority, StartupOption.IsChecked == true, selectedMode, customCpus);
            updated.Save();
            preferences = updated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Feedback.Text = $"Nie zapisano ustawień: {ex.Message}";
            return;
        }

        monitor.Retry();
        Scan();
        Feedback.Text = "Zapisano profil i priorytet.";
        if (!StartupOption.IsEnabled)
            return;

        try
        {
            Preferences.SetStartup(preferences.StartWithWindows);
            Feedback.Text = "Zapisano ustawienia.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            Feedback.Text += $" Windows nie przyjął zmiany autostartu: {ex.Message}";
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        monitor.Retry();
        Scan();
    }

    private ulong ResolveMask(Preferences settings)
    {
        if (settings.Affinity == AffinityMode.Unchanged)
            return 0;
        if (settings.Affinity != AffinityMode.Custom)
            return topology.ProfileMask(settings.Affinity);

        return topology.Cores.Length != 0 &&
            AffinitySelection.TryParse(settings.CustomCpus ?? "", AffinitySelection.ActiveMask, out ulong mask)
            ? mask : 0;
    }

    private static IEnumerable<int> CpuNumbers(ulong mask) =>
        Enumerable.Range(0, 64).Where(cpu => (mask & (1UL << cpu)) != 0);

    private void BuildCpuMap()
    {
        CpuName.Text = topology.Name;
        CpuSummary.Text = topology.Cores.Length == 0 ? "Mapa wątków niedostępna" :
            $"{topology.Cores.Length} rdzeni  /  {CpuNumbers(AffinitySelection.ActiveMask).Count()} wątków";
        TopologyNote.Text = topology.Ccds.Length == 2 && !topology.HasSmt
            ? "Windows udostępnia jeden wątek na rdzeń. Profile + SMT są niedostępne."
            : topology.Note;

        if (topology.Ccds.Length == 2)
        {
            for (int i = 0; i < topology.Ccds.Length; i++)
            {
                ulong domain = topology.Ccds[i];
                AddCpuGroup($"CCD{i}", topology.Cores.Where(core => (core & domain) == core));
            }
        }
        else if (topology.Cores.Length != 0)
        {
            AddCpuGroup("Rdzenie fizyczne", topology.Cores);
        }
    }

    private void AddCpuGroup(string label, IEnumerable<ulong> cores)
    {
        CpuGroups.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7)
        });
        var rows = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
        CpuGroups.Children.Add(rows);
        foreach (ulong core in cores)
        {
            int[] cpus = CpuNumbers(core).ToArray();
            int coreIndex = Array.IndexOf(topology.Cores, core);
            var card = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
            rows.Children.Add(new Border
            {
                Child = card,
                Background = new SolidColorBrush(Color.FromRgb(33, 33, 36)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 1, 0),
                Margin = new Thickness(0, 0, 8, 8)
            });
            card.Children.Add(new TextBlock
            {
                Text = $"RDZEŃ {coreIndex:00}",
                FontSize = 10,
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 5)
            });
            var threads = new WrapPanel();
            card.Children.Add(threads);
            foreach (int cpu in cpus)
            {
                var button = new ToggleButton
                {
                    Content = $"CPU {cpu}",
                    Style = (Style)FindResource("Tile"),
                    MinWidth = 60,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                AutomationProperties.SetName(button, $"Rdzeń {coreIndex}, procesor logiczny {cpu}");
                button.Click += (_, _) =>
                {
                    CustomOption.IsChecked = true;
                    UpdateSelectionSummary();
                };
                threadButtons.Add(cpu, button);
                threads.Children.Add(button);
            }
        }
    }

    private void Profile_Checked(object sender, RoutedEventArgs e)
    {
        selectedMode = profiles.First(entry => ReferenceEquals(entry.Value, sender)).Key;
        if (selectedMode == AffinityMode.Custom)
            UpdateSelectionSummary();
        else
            SetThreadSelection(selectedMode == AffinityMode.Unchanged ? 0 : topology.ProfileMask(selectedMode));
    }

    private void SetThreadSelection(ulong mask)
    {
        foreach (var (cpu, button) in threadButtons)
            button.IsChecked = (mask & (1UL << cpu)) != 0;
        UpdateSelectionSummary();
    }

    private ulong SelectedMask() => threadButtons.Aggregate(0UL,
        (mask, entry) => entry.Value.IsChecked == true ? mask | (1UL << entry.Key) : mask);

    private void UpdateSelectionSummary()
    {
        int count = threadButtons.Values.Count(button => button.IsChecked == true);
        SelectionSummary.Text = selectedMode == AffinityMode.Unchanged
            ? "Affinity: bez zmian"
            : $"Zaznaczone wątki: {count} z {threadButtons.Count}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        const int useImmersiveDarkMode = 20;
        int enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, useImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (exiting)
            return;

        e.Cancel = true;
        Hide();
    }

    private void Quit()
    {
        exiting = true;
        timer.Stop();
        tray.Dispose();
        trayMenu.Dispose();
        trayIcon.Dispose();
        Application.Current.Shutdown();
    }
}



