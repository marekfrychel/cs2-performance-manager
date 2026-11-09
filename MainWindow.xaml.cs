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
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;

namespace CS2PerformanceManager;

public partial class MainWindow : Window
{
    private readonly GameMonitor monitor;
    private readonly DispatcherTimer timer;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip trayMenu;
    private Preferences preferences = new(ProcessPriorityClass.Normal, Affinity: AffinityMode.Unchanged);
    private bool exiting;
    private readonly CpuLayout topology;
    private readonly Dictionary<int, ToggleButton> threadButtons = new();
    private Dictionary<AffinityMode, RadioButton> profiles = null!;
    private AffinityMode selectedMode;
    private readonly System.Drawing.Icon trayIcon;
    private static readonly int[] ZoomLevels = { 80, 90, 100, 110, 125, 150 };
    private int zoomIndex;

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

        int savedZoom = Array.IndexOf(ZoomLevels, preferences.ZoomPercent);
        SetZoom(savedZoom >= 0 ? savedZoom : Array.IndexOf(ZoomLevels, 100));

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
            if (mode is AffinityMode.Custom or AffinityMode.Unchanged)
                continue;

            button.Visibility = button.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            int group = mode is AffinityMode.Ccd0 or AffinityMode.Ccd0Smt ? 0 : 1;
            bool smt = mode is AffinityMode.Ccd0Smt or AffinityMode.Ccd1Smt;
            button.Content = $"{topology.GroupName(group)} {(smt ? "+" : "−")} SMT";
        }
        AdvancedPanel.Header = topology.Groups.Length == 0 ? "Priorytet i autostart" :
            topology.NamedCcds ? "Profile CCD i priorytet" : "Profile L3 i priorytet";
        OneThreadButton.IsEnabled = threadButtons.Count != 0;

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

        UpdateSavedSelection();

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
        var iconResource = Application.GetResourceStream(new Uri("/CS2PerformanceManager;component/App.ico", UriKind.Relative));
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
            Feedback.Text = "Zaznacz co najmniej jeden wątek albo wybierz „Windows”.";
            return;
        }

        string customCpus = string.Join(", ", CpuNumbers(mask));
        try
        {
            var updated = new Preferences(priority, StartupOption.IsChecked == true, selectedMode, customCpus, ZoomLevels[zoomIndex]);
            updated.Save();
            preferences = updated;
            UpdateSavedSelection();
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
        TopologyNote.Text = topology.Note;
        TopologyNote.Visibility = topology.Note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (topology.Groups.Length != 0)
        {
            for (int i = 0; i < topology.Groups.Length; i++)
            {
                ulong domain = topology.Groups[i];
                AddCpuGroup(topology.GroupName(i), topology.Cores.Where(core => (core & domain) == core));
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
            var card = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
            rows.Children.Add(card);
            card.Children.Add(new TextBlock
            {
                Text = $"Rdzeń {coreIndex}",
                FontSize = 12,
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
                    MinWidth = 51,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                AutomationProperties.SetName(button, $"Rdzeń {coreIndex}, procesor logiczny {cpu}");
                button.Click += (_, _) =>
                {
                    CustomOption.IsChecked = true;
                    AnimateReveal(button);
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
        if (selectedMode != AffinityMode.Unchanged)
            MapPanel.IsExpanded = true;
        if (selectedMode == AffinityMode.Custom)
            UpdateSelectionSummary();
        else
            SetThreadSelection(selectedMode == AffinityMode.Unchanged ? 0 : topology.ProfileMask(selectedMode));
    }

    private void OpenMap_Click(object sender, RoutedEventArgs e) => MapPanel.IsExpanded = true;

    private void OneThread_Click(object sender, RoutedEventArgs e)
    {
        CustomOption.IsChecked = true;
        SetThreadSelection(topology.OneThreadPerCore());
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(zoomIndex - 1);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(zoomIndex + 1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(Array.IndexOf(ZoomLevels, 100));

    private void SetZoom(int index)
    {
        zoomIndex = Math.Clamp(index, 0, ZoomLevels.Length - 1);
        int percent = ZoomLevels[zoomIndex];
        UiScale.ScaleX = UiScale.ScaleY = percent / 100.0;
        ZoomReset.Content = $"{percent}%";
        ZoomOut.IsEnabled = zoomIndex > 0;
        ZoomIn.IsEnabled = zoomIndex < ZoomLevels.Length - 1;
    }

    private void Section_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) ||
            sender is not Expander { Content: UIElement content })
            return;

        AnimateReveal(content);
        if (SystemParameters.ClientAreaAnimation)
        {
            var slide = new TranslateTransform();
            content.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
        }
    }

    private static void AnimateReveal(UIElement element)
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(.65, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
    }

    private void SetThreadSelection(ulong mask)
    {
        foreach (var (cpu, button) in threadButtons)
        {
            bool isSelected = (mask & (1UL << cpu)) != 0;
            if (button.IsChecked == isSelected)
                continue;

            button.IsChecked = isSelected;
            if (button.IsVisible)
                AnimateReveal(button);
        }
        UpdateSelectionSummary();
    }

    private ulong SelectedMask() => threadButtons.Aggregate(0UL,
        (mask, entry) => entry.Value.IsChecked == true ? mask | (1UL << entry.Key) : mask);

    private void UpdateSelectionSummary()
    {
        ulong mask = SelectedMask();
        SelectionSummary.Text = selectedMode == AffinityMode.Unchanged
            ? "Wybrane: Windows"
            : "Wybrane: " + DescribeSelection(mask);
    }

    private void UpdateSavedSelection()
    {
        ulong mask = ResolveMask(preferences);
        SavedSelection.Text = preferences.Affinity == AffinityMode.Unchanged
            ? "Zapisane dla CS2: Windows"
            : mask == 0 ? "Zapisany wybór wątków jest niedostępny."
            : $"Zapisane dla CS2: {profiles[preferences.Affinity].Content} · {DescribeSelection(mask)}";
        SavedSelection.ToolTip = mask == 0 ? null : "CPU " + string.Join(", ", CpuNumbers(mask));
    }

    private string DescribeSelection(ulong mask)
    {
        int cores = topology.Cores.Count(core => (core & mask) != 0);
        int threads = System.Numerics.BitOperations.PopCount(mask);
        return $"{cores} rdzeni · {threads} wątków";
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









