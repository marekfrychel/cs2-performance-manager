using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
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
    private readonly ulong ccdMask;
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

        CcdOption.IsChecked = preferences.Affinity == AffinityMode.Ccd1;
        CustomOption.IsChecked = preferences.Affinity == AffinityMode.Custom;
        UnchangedOption.IsChecked = preferences.Affinity == AffinityMode.Unchanged;
        CustomCpus.Text = preferences.CustomCpus ?? "";
        AvailableCpus.Text = AffinitySelection.ActiveMask == 0
            ? "Ręczny wybór wymaga jednej grupy procesorów."
            : "Dostępne: " + string.Join(", ", Enumerable.Range(0, 64)
                .Where(cpu => (AffinitySelection.ActiveMask & (1UL << cpu)) != 0));

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

        var topology = CpuTopology.Detect();
        TopologyText.Text = topology.Description;
        ccdMask = topology.Mask;
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
            ulong mask = 0;
            if (preferences.Affinity == AffinityMode.Ccd1)
                mask = ccdMask;
            else if (preferences.Affinity == AffinityMode.Custom &&
                     !AffinitySelection.TryParse(preferences.CustomCpus ?? "", AffinitySelection.ActiveMask, out mask))
                mask = 0;

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

        var mode = CustomOption.IsChecked == true ? AffinityMode.Custom :
            UnchangedOption.IsChecked == true ? AffinityMode.Unchanged : AffinityMode.Ccd1;
        if (mode == AffinityMode.Custom &&
            !AffinitySelection.TryParse(CustomCpus.Text, AffinitySelection.ActiveMask, out _))
        {
            Feedback.Text = "Wpisz dostępne numery CPU oddzielone przecinkami.";
            CustomCpus.Focus();
            return;
        }

        try
        {
            var updated = new Preferences(priority, StartupOption.IsChecked == true, mode, CustomCpus.Text.Trim());
            updated.Save();
            preferences = updated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Feedback.Text = $"Nie zapisano priorytetu: {ex.Message}";
            return;
        }

        monitor.Retry();
        Scan();
        Feedback.Text = "Zapisano priorytet.";
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

    private void Affinity_Checked(object sender, RoutedEventArgs e)
    {
        if (CustomPanel != null)
            CustomPanel.Visibility = CustomOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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


