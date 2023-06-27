﻿using ColorControl.Services.AMD;
using ColorControl.Services.GameLauncher;
using ColorControl.Services.LG;
using ColorControl.Services.NVIDIA;
using ColorControl.Services.Samsung;
using ColorControl.Shared.Common;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ColorControl.Services.Common
{
    public class ServiceManager
    {
        internal NvService NvService { get; set; }
        internal LgService LgService { get; set; }
        internal AmdService AmdService { get; set; }
        internal GameService GameService { get; set; }
        internal SamsungService SamsungService { get; set; }

        public async Task<bool> HandleExternalServiceAsync(string serviceName, string[] parameters)
        {
            if (string.IsNullOrEmpty(serviceName) || parameters.Length == 0)
            {
                return false;
            }

            if (NvService != null && serviceName.Equals("NvPreset", StringComparison.OrdinalIgnoreCase))
            {
                return await NvService.ApplyPreset(parameters[0]);
            }
            if (NvService != null && serviceName.Equals("GsyncEnabled", StringComparison.OrdinalIgnoreCase))
            {
                return NvService.IsGsyncEnabled();
            }
            if (AmdService != null && serviceName.Equals("AmdPreset", StringComparison.OrdinalIgnoreCase))
            {
                return await AmdService.ApplyPreset(parameters[0]);
            }
            if (LgService != null && serviceName.Equals("LgPreset", StringComparison.OrdinalIgnoreCase))
            {
                await LgService.ApplyPreset(parameters[0]);

                return true;
            }
            if (SamsungService != null && serviceName.Equals("SamsungPreset", StringComparison.OrdinalIgnoreCase))
            {
                await SamsungService.ApplyPreset(parameters[0]);

                return true;
            }

            if (serviceName.Equals("StartProgram", StringComparison.OrdinalIgnoreCase))
            {
                Utils.StartProcess(parameters[0], parameters.Length > 1 ? string.Join(" ", parameters.Skip(1)) : null, setWorkingDir: true);

                return true;
            }

            return false;
        }

        public void Save()
        {
            NvService?.GlobalSave();
            AmdService?.GlobalSave();
            LgService?.GlobalSave();
            GameService?.GlobalSave();
            SamsungService?.GlobalSave();
        }
    }
}
