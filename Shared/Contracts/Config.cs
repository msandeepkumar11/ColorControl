﻿namespace ColorControl.Shared.Contracts
{
    public enum ElevationMethod
    {
        None = 0,
        RunAsAdmin = 1,
        UseService = 2,
        UseElevatedProcess = 3
    }

    public class Config
    {
        public bool StartMinimized { get; set; }
        public bool MinimizeOnClose { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool CheckForUpdates { get; set; }
        public bool AutoInstallUpdates { get; set; }
        public bool UseDarkMode { get; set; }
        public int DisplaySettingsDelay { get; set; }
        public string ScreenSaverShortcut { get; set; }
        public int FormWidth { get; set; }
        public int FormHeight { get; set; }
        public int NvPresetId_ApplyOnStartup { get; set; }
        public int AmdPresetId_ApplyOnStartup { get; set; }
        public bool FixChromeFonts { get; set; }
        public bool UseGdiScaling { get; set; }
        public ListViewSortState NvPresetsSortState { get; set; }
        public ListViewSortState AmdPresetsSortState { get; set; }
        public ListViewSortState LgPresetsSortState { get; set; }
        public ListViewSortState SamsungPresetsSortState { get; set; }
        public ListViewSortState GamePresetsSortState { get; set; }
        public string NvQuickAccessShortcut { get; set; }
        public string AmdQuickAccessShortcut { get; set; }
        public string LgQuickAccessShortcut { get; set; }
        public string GameQuickAccessShortcut { get; set; }
        public bool UseDedicatedElevatedProcess { get; set; }
        public ElevationMethod ElevationMethod { get; set; }
        public bool ElevationMethodAsked { get; set; }
        public int ProcessMonitorPollingInterval { get; set; }
        public string LogLevel { get; set; }
        public List<Module> Modules { get; set; }
        public bool UseRawInput { get; set; }

        public Config()
        {
            LogLevel = "DEBUG";
            DisplaySettingsDelay = 1000;
            ScreenSaverShortcut = string.Empty;
            FormWidth = 900;
            FormHeight = 600;
            MinimizeToTray = true;
            CheckForUpdates = true;
            FixChromeFonts = false;
            UseGdiScaling = true;
            UseDedicatedElevatedProcess = false;
            ElevationMethod = ElevationMethod.None;
            ElevationMethodAsked = false;
            ProcessMonitorPollingInterval = 1000;
            LgPresetsSortState = new ListViewSortState();
            NvPresetsSortState = new ListViewSortState();
            AmdPresetsSortState = new ListViewSortState();
            SamsungPresetsSortState = new ListViewSortState();
            GamePresetsSortState = new ListViewSortState();
            Modules = new List<Module>();
        }
    }
}
