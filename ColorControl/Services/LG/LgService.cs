﻿using ColorControl.Services.Common;
using ColorControl.Services.EventDispatcher;
using ColorControl.Shared.Common;
using ColorControl.Shared.Contracts;
using ColorControl.Shared.Native;
using ColorControl.Shared.Services;
using LgTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Newtonsoft.Json;
using NWin32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ColorControl.Services.LG
{
    class LgService : ServiceBase<LgPreset>
    {
        protected static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public static string LgListAppsJson = "listApps.json";

        public override string ServiceName => "LG";

        public List<LgDevice> Devices { get; private set; }
        public LgDevice SelectedDevice
        {
            get { return _selectedDevice; }
            set
            {
                _selectedDevice = value;
                Config.PreferredMacAddress = _selectedDevice != null ? _selectedDevice.MacAddress : null;

                SelectedDeviceChangedEvent?.Invoke(_selectedDevice, EventArgs.Empty);
            }
        }
        public event EventHandler SelectedDeviceChangedEvent;

        public LgServiceConfig Config { get; private set; }

        protected override string PresetsBaseFilename => "LgPresets.json";

        private string _configFilename;
        private bool _allowPowerOn;
        private readonly PowerEventDispatcher _powerEventDispatcher;
        private readonly SessionSwitchDispatcher _sessionSwitchDispatcher;
        private readonly ProcessEventDispatcher _processEventDispatcher;
        private readonly ServiceManager _serviceManager;
        private LgDevice _selectedDevice;
        private string _rcButtonsFilename;
        private List<LgPreset> _remoteControlButtons;
        private List<LgApp> _lgApps = new List<LgApp>();

        private bool _poweredOffByScreenSaver;
        private int _poweredOffByScreenSaverProcessId;
        private LgPreset _lastTriggeredPreset;
        private RestartDetector _restartDetector;
        private readonly WinApiService _winApiService;
        private readonly WinApiAdminService _winApiAdminService;
        private SynchronizationContext _syncContext;

        public ProcessChangedEventArgs CurrentProcessEvent => _processEventDispatcher?.MonitorContext;

        public LgService(AppContextProvider appContextProvider, PowerEventDispatcher powerEventDispatcher, SessionSwitchDispatcher sessionSwitchDispatcher, ProcessEventDispatcher processEventDispatcher, ServiceManager serviceManager, RestartDetector restartDetector, WinApiService winApiService, WinApiAdminService winApiAdminService) : base(appContextProvider)
        {
            _allowPowerOn = appContextProvider.GetAppContext().StartUpParams.RunningFromScheduledTask;
            _powerEventDispatcher = powerEventDispatcher;
            _sessionSwitchDispatcher = sessionSwitchDispatcher;
            _processEventDispatcher = processEventDispatcher;
            _serviceManager = serviceManager;
            LgPreset.LgApps = _lgApps;

            _restartDetector = restartDetector;
            _winApiService = winApiService;
            _winApiAdminService = winApiAdminService;
            _syncContext = appContextProvider.GetAppContext().SynchronizationContext;
            LgTvApiCore.SyncContext = _syncContext;

            LoadConfig();
            LoadPresets();
            LoadRemoteControlButtons();
        }

        ~LgService()
        {
            GlobalSave();
        }

        public static async Task<bool> ExecutePresetAsync(string presetName)
        {
            try
            {
                var lgService = Program.ServiceProvider.GetRequiredService<LgService>();

                await lgService.RefreshDevices(afterStartUp: true);

                var result = await lgService.ApplyPreset(presetName);

                if (!result)
                {
                    Console.WriteLine("Preset not found or error while executing.");
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error executing preset: " + ex.ToLogString());
                return false;
            }
        }

        public void GlobalSave()
        {
            SaveConfig();
            SavePresets();
            SaveRemoteControlButtons();

            SendConfigToService();
        }

        protected override List<LgPreset> GetDefaultPresets()
        {
            List<LgPreset> presets = null;

            var defaultPresetsFileName = Path.Combine(Directory.GetCurrentDirectory(), "LgPresets.json");
            if (File.Exists(defaultPresetsFileName))
            {
                var json = File.ReadAllText(defaultPresetsFileName);

                presets = JsonConvert.DeserializeObject<List<LgPreset>>(json);
            }

            return presets;
        }

        private void LoadConfig()
        {
            _configFilename = Path.Combine(_dataPath, "LgConfig.json");
            try
            {
                if (File.Exists(_configFilename))
                {
                    Config = JsonConvert.DeserializeObject<LgServiceConfig>(File.ReadAllText(_configFilename));
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"LoadConfig: {ex.Message}");
            }
            Config ??= new LgServiceConfig();

            LgPreset.LgDevices = Config.Devices;
        }

        private void LoadRemoteControlButtons()
        {
            var defaultButtons = GenerateDefaultRemoteControlButtons();

            _rcButtonsFilename = Path.Combine(_dataPath, "LgRemoteControlButtons.json");
            //if (File.Exists(_rcButtonsFilename))
            //{
            //    var json = File.ReadAllText(_rcButtonsFilename);

            //    _remoteControlButtons = _JsonDeserializer.Deserialize<List<LgPreset>>(json);

            //    var missingButtons = defaultButtons.Where(b => !_remoteControlButtons.Any(x => x.name.Equals(b.name)));
            //    _remoteControlButtons.AddRange(missingButtons);
            //}
            //else
            {
                _remoteControlButtons = defaultButtons;
            }
        }

        public List<LgPreset> GetRemoteControlButtons()
        {
            return _remoteControlButtons;
        }

        private List<LgPreset> GenerateDefaultRemoteControlButtons()
        {
            var list = new List<LgPreset>();

            for (var i = 1; i < 11; i++)
            {
                var button = i < 10 ? i : 0;
                list.Add(GeneratePreset(button.ToString()));
            }

            list.Add(GeneratePreset("Power", step: "POWER"));
            list.Add(GeneratePreset("Wol", step: "WOL"));
            list.Add(GeneratePreset("Home", appId: "com.webos.app.home", key: Keys.H));
            list.Add(GeneratePreset("Settings", appId: "com.palm.app.settings", key: Keys.S));
            list.Add(GeneratePreset("TV Guide", appId: "com.webos.app.livemenu"));
            list.Add(GeneratePreset("List", step: "LIST"));
            list.Add(GeneratePreset("SAP", step: "SAP"));

            list.Add(GeneratePreset("Vol +", step: "VOLUMEUP", key: Keys.VolumeUp));
            list.Add(GeneratePreset("Vol -", step: "VOLUMEDOWN", key: Keys.VolumeDown));
            list.Add(GeneratePreset("Mute", step: "MUTE", key: Keys.VolumeMute));
            list.Add(GeneratePreset("Channel +", step: "CHANNELUP", key: Keys.MediaNextTrack));
            list.Add(GeneratePreset("Channel -", step: "CHANNELDOWN", key: Keys.MediaPreviousTrack));

            list.Add(GeneratePreset("Up", step: "UP", key: Keys.Up));
            list.Add(GeneratePreset("Down", step: "DOWN", key: Keys.Down));
            list.Add(GeneratePreset("Left", step: "LEFT", key: Keys.Left));
            list.Add(GeneratePreset("Right", step: "RIGHT", key: Keys.Right));

            list.Add(GeneratePreset("Enter", step: "ENTER", key: Keys.Enter));
            list.Add(GeneratePreset("Back", step: "BACK", key: Keys.Back));
            list.Add(GeneratePreset("Exit", step: "EXIT", key: Keys.Escape));

            list.Add(GeneratePreset("Netflix", appId: "netflix"));
            list.Add(GeneratePreset("Inputs", appId: "com.webos.app.homeconnect"));
            list.Add(GeneratePreset("Amazon Prime", appId: "amazon"));

            list.Add(GeneratePreset("Red", step: "RED"));
            list.Add(GeneratePreset("Green", step: "GREEN"));
            list.Add(GeneratePreset("Yellow", step: "YELLOW"));
            list.Add(GeneratePreset("Blue", step: "BLUE"));

            list.Add(GeneratePreset("Rakuten TV", appId: "ui30"));
            list.Add(GeneratePreset("Play", step: "PLAY"));
            list.Add(GeneratePreset("Pause", step: "PAUSE"));

            return list;
        }

        private LgPreset GeneratePreset(string name, string step = null, string appId = null, Keys key = Keys.None)
        {
            var preset = new LgPreset();

            var cvt = new KeysConverter();
            var shortcut = key != Keys.None ? (string)cvt.ConvertTo(key, typeof(string)) : null;

            preset.name = name;
            preset.appId = appId;
            preset.shortcut = shortcut;
            if (appId == null)
            {
                preset.steps.Add(step == null ? name : step);
            }

            return preset;
        }

        private void SavePresets()
        {
            Utils.WriteObject(_presetsFilename, _presets);
        }

        private void SaveConfig()
        {
            Config.PowerOnAfterStartup = Devices?.Any(d => d.PowerOnAfterStartup) ?? false;

            Utils.WriteObject(_configFilename, Config);
        }

        private void SaveRemoteControlButtons()
        {
            Utils.WriteObject(_rcButtonsFilename, _remoteControlButtons);
        }

        public async Task RefreshDevices(bool connect = true, bool afterStartUp = false)
        {
            Devices = Config.Devices;

            if (!_appContextProvider.GetAppContext().StartUpParams.NoDeviceRefresh)
            {
                var customIpAddresses = Devices.Where(d => d.IsCustom).Select(d => d.IpAddress);

                var pnpDevices = await Utils.GetPnpDevices(Config.DeviceSearchKey);

                var autoDevices = pnpDevices.Where(p => !customIpAddresses.Contains(p.IpAddress)).Select(d => new LgDevice(d.Name, d.IpAddress, d.MacAddress, false)).ToList();
                var autoIpAddresses = pnpDevices.Select(d => d.IpAddress);

                Devices.RemoveAll(d => !d.IsCustom && !autoIpAddresses.Contains(d.IpAddress));

                var newAutoDevices = autoDevices.Where(ad => ad.IpAddress != null && !Devices.Any(d => d.IpAddress != null && d.IpAddress.Equals(ad.IpAddress)));
                Devices.AddRange(newAutoDevices);
            }

            if (Devices.Any())
            {
                var preferredDevice = Devices.FirstOrDefault(x => x.MacAddress != null && x.MacAddress.Equals(Config.PreferredMacAddress)) ?? Devices[0];

                SelectedDevice = preferredDevice;
            }
            else
            {
                SelectedDevice = null;
            }

            if (afterStartUp && SelectedDevice == null && Config.PowerOnAfterStartup && !string.IsNullOrEmpty(Config.PreferredMacAddress))
            {
                Logger.Debug("No device has been found, trying to wake it first...");

                var tempDevice = new LgDevice("Test", string.Empty, Config.PreferredMacAddress);
                tempDevice.Wake();

                await Task.Delay(4000);
                await RefreshDevices();

                return;
            }

            foreach (var device in Devices)
            {
                device.PowerStateChangedEvent += LgDevice_PowerStateChangedEvent;
                device.PictureSettingsChangedEvent += Device_PictureSettingsChangedEvent;
            }

            if (connect && SelectedDevice != null)
            {
                if (_allowPowerOn)
                {
                    WakeAfterStartupOrResume();
                }
                else
                {
                    var _ = SelectedDevice.Connect();
                }
            }
        }

        private void Device_PictureSettingsChangedEvent(object sender, EventArgs e)
        {
            //
        }

        public async Task<bool> ApplyPreset(string presetName)
        {
            var preset = _presets.FirstOrDefault(p => p.name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset != null)
            {
                return await ApplyPreset(preset);
            }
            else
            {
                return false;
            }
        }

        public override async Task<bool> ApplyPreset(LgPreset preset)
        {
            return await ApplyPreset(preset, false);
        }

        public async Task<bool> ApplyPreset(LgPreset preset, bool reconnect = false)
        {
            var device = GetPresetDevice(preset);

            if (device == null)
            {
                Logger.Debug("Cannot apply preset: no device has been selected");
                return false;
            }

            var result = await device.ExecutePreset(preset, reconnect, Config);

            _lastAppliedPreset = preset;

            return result;
        }

        public LgDevice GetPresetDevice(LgPreset preset)
        {
            var device = string.IsNullOrEmpty(preset.DeviceMacAddress)
                ? SelectedDevice
                : Devices.FirstOrDefault(d => d.MacAddress?.Equals(preset.DeviceMacAddress, StringComparison.OrdinalIgnoreCase) ?? false);
            return device;
        }

        public async Task<LgWebOsMouseService> GetMouseAsync()
        {
            return await SelectedDevice?.GetMouseAsync();
        }

        public async Task RefreshApps(bool force = false)
        {
            if (SelectedDevice == null)
            {
                Logger.Debug("Cannot refresh apps: no device has been selected");
                return;
            }

            var apps = await SelectedDevice.GetApps(force);

            if (apps.Any())
            {
                _lgApps.Clear();
                _lgApps.AddRange(apps);
            }
        }

        public List<LgApp> GetApps()
        {
            return _lgApps;
        }

        internal async Task<bool> PowerOff()
        {
            return await SelectedDevice?.PowerOff();
        }

        internal bool WakeSelectedDevice()
        {
            return SelectedDevice?.Wake() ?? false;
        }

        internal void WakeAfterStartupOrResume(PowerOnOffState state = PowerOnOffState.StartUp, bool checkUserSession = true)
        {
            Devices.ForEach(d => d.ClearPowerOffTask());

            var wakeDevices = Devices.Where(d => state == PowerOnOffState.StartUp && d.PowerOnAfterStartup ||
                                                 state == PowerOnOffState.Resume && d.PowerOnAfterResume ||
                                                 state == PowerOnOffState.ScreenSaver && d.PowerOnAfterScreenSaver);

            PowerOnDevicesTask(wakeDevices, state, checkUserSession);
        }

        public void InstallEventHandlers()
        {
            _powerEventDispatcher.RegisterEventHandler(PowerEventDispatcher.Event_Suspend, PowerModeChanged);
            _powerEventDispatcher.RegisterEventHandler(PowerEventDispatcher.Event_Resume, PowerModeChanged);
            _powerEventDispatcher.RegisterEventHandler(PowerEventDispatcher.Event_Shutdown, PowerModeChanged);
            _powerEventDispatcher.RegisterEventHandler(PowerEventDispatcher.Event_MonitorOff, PowerModeChanged);
            _powerEventDispatcher.RegisterEventHandler(PowerEventDispatcher.Event_MonitorOn, PowerModeChanged);

            //SystemEvents.SessionEnded -= SessionEnded;
            //SystemEvents.SessionEnded += SessionEnded;

            _sessionSwitchDispatcher.RegisterEventHandler(SessionSwitchDispatcher.Event_SessionSwitch, SessionSwitched);

            _processEventDispatcher.RegisterAsyncEventHandler(ProcessEventDispatcher.Event_ProcessChanged, ProcessChanged);
        }

        private void SessionSwitched(object sender, SessionSwitchEventArgs e)
        {
            if (!_sessionSwitchDispatcher.UserLocalSession)
            {
                return;
            }

            if (_poweredOffByScreenSaver)
            {
                Logger.Debug("User switched to local and screen was powered off due to screensaver: waking up");
                _poweredOffByScreenSaver = false;
                _poweredOffByScreenSaverProcessId = 0;
                WakeAfterStartupOrResume();
            }
            else
            {
                Logger.Debug("User switched to local but screen was not powered off by screensaver: NOT waking up");
            }
        }

        private void PowerModeChanged(object sender, PowerStateChangedEventArgs e)
        {
            Logger.Debug($"PowerModeChanged: {e.State}");

            switch (e.State)
            {
                case PowerOnOffState.Resume:
                    {
                        WakeAfterStartupOrResume(PowerOnOffState.Resume);
                        break;
                    }
                case PowerOnOffState.StandBy:
                    {
                        var devices = Devices.Where(d => d.PowerOffOnStandby);
                        PowerOffDevices(devices, PowerOnOffState.StandBy);
                        break;
                    }
                case PowerOnOffState.ShutDown:
                    {
                        var devices = Devices.Where(d => d.PowerOffOnShutdown);

                        PowerOffDevices(devices);
                        break;
                    }

                case PowerOnOffState.MonitorOff:
                    {
                        var devices = Devices.Where(d => d.PowerOffByWindows).ToList();
                        PowerOffDevices(devices, PowerOnOffState.MonitorOff);
                        break;
                    }
                case PowerOnOffState.MonitorOn:
                    {
                        var devices = Devices.Where(d => d.PowerOnByWindows).ToList();
                        PowerOnDevicesTask(devices);
                        break;
                    }
            }
        }

        private void SessionEnded(object sender, SessionEndedEventArgs e)
        {
            //if (e.Reason == SessionEndReasons.SystemShutdown)
            //{
            //    Logger.Debug($"SessionEnded: {e.Reason}");

            //    if (Config.PowerOffOnShutdown)
            //    {
            //        PowerOff();
            //    }
            //}
        }

        private void LgDevice_PowerStateChangedEvent(object sender, EventArgs e)
        {
            if (!(sender is LgDevice device))
            {
                return;
            }

            if (device.CurrentState == LgDevice.PowerState.Active)
            {
                if (Config.SetSelectedDeviceByPowerOn)
                {
                    SelectedDevice = device;
                }
            }
        }

        public async Task ProcessChanged(object sender, ProcessChangedEventArgs args, CancellationToken token)
        {
            try
            {
                var wasConnected = Devices.Any(d => (d.PowerOffOnScreenSaver || d.PowerOnAfterScreenSaver) && d.IsConnected());

                var applicableDevices = Devices.Where(d => d.PowerOffOnScreenSaver || d.PowerOnAfterScreenSaver || d.TriggersEnabled && _presets.Any(p => p.Triggers.Any(t => t.Trigger != PresetTriggerType.None) &&
                    ((string.IsNullOrEmpty(p.DeviceMacAddress) && d == SelectedDevice) || p.DeviceMacAddress.Equals(d.MacAddress, StringComparison.OrdinalIgnoreCase))));

                if (!applicableDevices.Any())
                {
                    return;
                }

                if (_poweredOffByScreenSaver && !_sessionSwitchDispatcher.UserLocalSession)
                {
                    return;
                }

                var processes = args.RunningProcesses;

                if (_poweredOffByScreenSaver)
                {
                    if (processes.Any(p => p.Id == _poweredOffByScreenSaverProcessId))
                    {
                        return;
                    }

                    var process = processes.FirstOrDefault(p => p.ProcessName.ToLowerInvariant().EndsWith(".scr"));

                    if (process != null)
                    {
                        Logger.Debug($"Detected screen saver process switch from {_poweredOffByScreenSaverProcessId} to {process.Id}");

                        _poweredOffByScreenSaverProcessId = process.Id;
                        return;
                    }

                    Logger.Debug("Screensaver stopped, waking");
                    _poweredOffByScreenSaver = false;
                    _poweredOffByScreenSaverProcessId = 0;

                    WakeAfterStartupOrResume(PowerOnOffState.ScreenSaver);

                    return;
                }

                var connectedDevices = applicableDevices.Where(d => d.IsConnected()).ToList();

                if (!connectedDevices.Any())
                {
                    if (wasConnected)
                    {
                        Logger.Debug("Process monitor: TV(s) where connected, but not any longer");
                        wasConnected = false;

                        return;
                    }
                    else
                    {
                        var devices = applicableDevices.ToList();
                        foreach (var device in devices)
                        {
                            Logger.Debug($"Process monitor: test connection with {device.Name}...");

                            var test = await device.TestConnection();
                            Logger.Debug("Process monitor: test connection result: " + test);
                        }

                        connectedDevices = applicableDevices.Where(d => d.IsConnected()).ToList();

                        if (!connectedDevices.Any())
                        {
                            return;
                        }
                    }
                }

                if (!wasConnected)
                {
                    Logger.Debug("Process monitor: TV(s) where not connected, but connection has now been established");
                    wasConnected = true;
                }

                if (connectedDevices.Any(d => d.PowerOffOnScreenSaver || d.PowerOnAfterScreenSaver))
                {
                    await HandleScreenSaverProcessAsync(processes, connectedDevices);
                }

                await ExecuteProcessPresets(args, connectedDevices);

            }
            catch (Exception ex)
            {
                Logger.Error("ProcessChanged: " + ex.ToLogString());
            }
        }

        private async Task ExecuteProcessPresets(ProcessChangedEventArgs context, IList<LgDevice> connectedDevices)
        {
            var triggerDevices = connectedDevices.Where(d => d.TriggersEnabled).ToList();

            if (context.IsScreenSaverActive)
            {
                return;
            }

            var selectedDevice = SelectedDevice;

            var presets = _presets.Where(p => p.Triggers.Any(t => t.Trigger == PresetTriggerType.ProcessSwitch) &&
                                        ((string.IsNullOrEmpty(p.DeviceMacAddress) && selectedDevice?.TriggersEnabled == true)
                                            || Devices.Any(d => d.TriggersEnabled && p.DeviceMacAddress.Equals(d.MacAddress, StringComparison.OrdinalIgnoreCase))));
            if (!presets.Any())
            {
                return;
            }

            var changedProcesses = new List<Process>();
            if (context.ForegroundProcess != null)
            {
                changedProcesses.Add(context.ForegroundProcess);
            }

            if (context.ForegroundProcess != null && context.ForegroundProcessIsFullScreen)
            {
                presets = presets.Where(p => p.Triggers.Any(t => t.Conditions.HasFlag(PresetConditionType.FullScreen)));
            }
            else if (context.StoppedFullScreen)
            {
                presets = presets.Where(p => p.Triggers.Any(t => !t.Conditions.HasFlag(PresetConditionType.FullScreen)));
            }

            var isHDRActive = triggerDevices.Any(d => d.IsUsingHDRPictureMode());

            var isGsyncActive = await _serviceManager.HandleExternalServiceAsync("GsyncEnabled", new[] { "" });

            var triggerContext = new PresetTriggerContext
            {
                IsHDRActive = isHDRActive,
                IsGsyncActive = isGsyncActive,
                ForegroundProcess = context.ForegroundProcess,
                ForegroundProcessIsFullScreen = context.ForegroundProcessIsFullScreen,
                ChangedProcesses = changedProcesses,
                IsNotificationDisabled = context.IsNotificationDisabled
            };

            var toApplyPresets = presets.Where(p => p.Triggers.Any(t => t.TriggerActive(triggerContext))).ToList();

            if (toApplyPresets.Any())
            {
                var toApplyPreset = toApplyPresets.FirstOrDefault(p => toApplyPresets.Count == 1 || p.Triggers.Any(t => !t.IncludedProcesses.Contains("*"))) ?? toApplyPresets.First();

                if (_lastTriggeredPreset != toApplyPreset)
                {
                    Logger.Debug($"Applying triggered preset: {toApplyPreset.name} because trigger conditions are met");

                    await ApplyPreset(toApplyPreset);

                    _lastTriggeredPreset = toApplyPreset;
                }
            }
        }

        private async Task HandleScreenSaverProcessAsync(IList<Process> processes, List<LgDevice> connectedDevices)
        {
            var process = processes.FirstOrDefault(p => p.ProcessName.ToLowerInvariant().EndsWith(".scr"));

            var powerOffDevices = connectedDevices.Where(d => d.PowerOffOnScreenSaver).ToList();

            if (process == null)
            {
                powerOffDevices.ForEach(d => d.ClearPowerOffTask());

                return;
            }

            var parent = process.Parent(processes);

            // Ignore manually started screen savers if no devices prefer this
            if (parent?.ProcessName.Contains("winlogon") != true && !connectedDevices.Any(d => d.HandleManualScreenSaver))
            {
                return;
            }

            Logger.Debug($"Screensaver started: {process.ProcessName}, parent: {parent.ProcessName}");
            try
            {
                foreach (var device in powerOffDevices)
                {
                    Logger.Debug($"Screensaver check: test connection with {device.Name}...");

                    var test = await device.TestConnection();
                    Logger.Debug("Screensaver check: test connection result: " + test);
                    if (!test)
                    {
                        continue;
                    }

                    if (device.ScreenSaverMinimalDuration > 0)
                    {
                        Logger.Debug($"Screensaver check: powering off tv {device.Name} in {device.ScreenSaverMinimalDuration} seconds because of screensaver");

                        _poweredOffByScreenSaver = true;

                        device.PowerOffIn(device.ScreenSaverMinimalDuration);

                        continue;
                    }

                    Logger.Debug($"Screensaver check: powering off tv {device.Name} because of screensaver");
                    try
                    {
                        _poweredOffByScreenSaver = await device.PerformActionOnScreenSaver().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException)
                    {
                        // Assume powering off was successful
                        _poweredOffByScreenSaver = true;
                        Logger.Error($"Screensaver check: timeout exception while powering off (probably connection closed)");
                    }
                    catch (Exception pex)
                    {
                        // Assume powering off was successful
                        _poweredOffByScreenSaver = true;
                        Logger.Error($"Screensaver check: exception while powering off (probably connection closed): " + pex.Message);
                    }
                    Logger.Debug($"Screensaver check: powered off tv {device.Name}");
                }

                _poweredOffByScreenSaverProcessId = process.Id;
            }
            catch (Exception e)
            {
                Logger.Error($"Screensaver check: can't power off: " + e.ToLogString());
            }
        }

        internal void AddCustomDevice(LgDevice device)
        {
            Devices.Add(device);
        }

        internal void RemoveCustomDevice(LgDevice device)
        {
            Devices.Remove(device);

            if (!string.IsNullOrEmpty(device.MacAddress))
            {
                var presets = _presets.Where(p => !string.IsNullOrEmpty(p.DeviceMacAddress) && p.DeviceMacAddress.Equals(device.MacAddress, StringComparison.OrdinalIgnoreCase)).ToList();
                presets.ForEach(preset =>
                {
                    preset.DeviceMacAddress = null;
                });
            }

            if (SelectedDevice == device)
            {
                SelectedDevice = Devices.Any() ? Devices.First() : null;
            }
        }

        public void PowerOffDevices(IEnumerable<LgDevice> devices, PowerOnOffState state = PowerOnOffState.ShutDown)
        {
            if (!devices.Any())
            {
                return;
            }

            if (NativeMethods.GetAsyncKeyState(NativeConstants.VK_CONTROL) < 0 || NativeMethods.GetAsyncKeyState(NativeConstants.VK_RCONTROL) < 0)
            {
                Logger.Debug("Not powering off because CONTROL-key is down");
                return;
            }

            var error = NativeMethods.SetThreadExecutionState(NativeConstants.ES_CONTINUOUS | NativeConstants.ES_SYSTEM_REQUIRED | NativeConstants.ES_AWAYMODE_REQUIRED);
            try
            {
                Logger.Debug($"SetThreadExecutionState: {error}, Thread#: {Environment.CurrentManagedThreadId}");
                if (state == PowerOnOffState.ShutDown)
                {
                    var sleep = Config.ShutdownDelay;

                    Logger.Debug($"PowerOffDevices: Waiting for a maximum of {sleep} milliseconds...");

                    while (sleep > 0 && _restartDetector?.PowerOffDetected == false)
                    {
                        Thread.Sleep(100);

                        if (_restartDetector != null && (_restartDetector.RestartDetected || _restartDetector.IsRebootInProgress()))
                        {
                            Logger.Debug("Not powering off because of a restart");
                            return;
                        }

                        sleep -= 100;
                    }
                }

                Logger.Debug("Powering off tv(s)...");
                var tasks = new List<Task>();
                foreach (var device in devices)
                {
                    var task = device.PowerOff(true, false);

                    tasks.Add(task);
                }

                if (state == PowerOnOffState.StandBy)
                {
                    var standByScript = Path.Combine(Program.DataDir, "StandByScript.bat");
                    if (File.Exists(standByScript))
                    {
                        _winApiAdminService.StartProcess(standByScript, hidden: true, wait: true);
                    }
                }

                // We can't use async here because we need to stay on the main thread...
                var _ = Task.WhenAll(tasks.ToArray()).ConfigureAwait(true);

                Logger.Debug("Done powering off tv(s)");
            }
            finally
            {
                var error2 = NativeMethods.SetThreadExecutionState(NativeConstants.ES_CONTINUOUS);
                Logger.Debug($"SetThreadExecutionState (reset): {error2}, Thread#: {Environment.CurrentManagedThreadId}");
            }
        }

        public void PowerOnDevicesTask(IEnumerable<LgDevice> devices, PowerOnOffState state = PowerOnOffState.StartUp, bool checkUserSession = true)
        {
            Task.Run(async () => await PowerOnDevices(devices, state, checkUserSession));
        }

        public async Task PowerOnDevices(IEnumerable<LgDevice> devices, PowerOnOffState state = PowerOnOffState.StartUp, bool checkUserSession = true)
        {
            if (!devices.Any())
            {
                return;
            }

            if (checkUserSession && !_sessionSwitchDispatcher.UserLocalSession)
            {
                Logger.Debug($"WakeAfterStartupOrResume: not waking because session info indicates no local session");
                return;
            }

            var resumeScript = Path.Combine(Program.DataDir, "ResumeScript.bat");

            var tasks = new List<Task>();

            foreach (var device in devices)
            {
                if (!device.PowerOnAfterManualPowerOff && device.PoweredOffBy == LgDevice.PowerOffSource.Manually)
                {
                    Logger.Debug($"[{device.Name}]: device was manually powered off by user, not powering on");
                    continue;
                }

                if (state == PowerOnOffState.ScreenSaver)
                {
                    tasks.Add(device.PerformActionAfterScreenSaver(Config.PowerOnRetries));
                    continue;
                }

                var task = device.WakeAndConnectWithRetries(Config.PowerOnRetries);
                tasks.Add(task);
            }

            await Task.WhenAll(tasks.ToArray());

            if (File.Exists(resumeScript))
            {
                _winApiAdminService.StartProcess(resumeScript, hidden: true);
            }
        }

        private void SendConfigToService()
        {
            if (!_winApiService.IsServiceRunning())
            {
                return;
            }

            var json = JsonConvert.SerializeObject(Config);

            var message = new SvcMessage
            {
                MessageType = SvcMessageType.SetLgConfig,
                Data = json
            };

            PipeUtils.SendMessage(message);
        }
    }
}