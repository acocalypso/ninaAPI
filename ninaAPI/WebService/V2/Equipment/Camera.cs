#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using EmbedIO.WebApi;
using EmbedIO;
using System;
using System.Threading.Tasks;
using EmbedIO.Routing;
using System.Drawing;
using System.Threading;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.PlateSolving;
using NINA.Profile.Interfaces;
using ninaAPI.Utility;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using System.Linq;
using System.Windows.Media.Imaging;
using System.IO;
using System.Collections.Generic;
using NINA.Image.ImageData;
using NINA.Image.ImageAnalysis;
using NINA.Core.Enum;
using System.Reflection;
using Accord;
using NINA.Equipment.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace ninaAPI.WebService.V2
{
    class CaptureResponse
    {
        public string Image { get; set; }
        public PlateSolveResult PlateSolveResult { get; set; }
    }

    class CameraInfoResponse : CameraInfo
    {
        public static CameraInfoResponse FromCam(ICameraMediator cam)
        {
            return new CameraInfoResponse()
            {
                Connected = cam.GetInfo().Connected,
                CanSetTemperature = cam.GetInfo().CanSetTemperature,
                HasDewHeater = cam.GetInfo().HasDewHeater,
                IsExposing = cam.GetInfo().IsExposing,
                PixelSize = cam.GetInfo().PixelSize,
                BinX = cam.GetInfo().BinX,
                BinY = cam.GetInfo().BinY,
                Battery = cam.GetInfo().Battery,
                Offset = cam.GetInfo().Offset,
                OffsetMin = cam.GetInfo().OffsetMin,
                OffsetMax = cam.GetInfo().OffsetMax,
                DefaultOffset = cam.GetInfo().DefaultOffset,
                USBLimit = cam.GetInfo().USBLimit,
                USBLimitMin = cam.GetInfo().USBLimitMin,
                USBLimitMax = cam.GetInfo().USBLimitMax,
                DefaultGain = cam.GetInfo().DefaultGain,
                GainMin = cam.GetInfo().GainMin,
                GainMax = cam.GetInfo().GainMax,
                CanSetGain = cam.GetInfo().CanSetGain,
                Gains = cam.GetInfo().Gains,
                CoolerOn = cam.GetInfo().CoolerOn,
                CoolerPower = cam.GetInfo().CoolerPower,
                HasShutter = cam.GetInfo().HasShutter,
                Temperature = cam.GetInfo().Temperature,
                TemperatureSetPoint = cam.GetInfo().TemperatureSetPoint,
                ReadoutModes = cam.GetInfo().ReadoutModes,
                ReadoutMode = cam.GetInfo().ReadoutMode,
                ReadoutModeForSnapImages = cam.GetInfo().ReadoutModeForSnapImages,
                ReadoutModeForNormalImages = cam.GetInfo().ReadoutModeForNormalImages,
                IsSubSampleEnabled = cam.GetInfo().IsSubSampleEnabled,
                SubSampleX = cam.GetInfo().SubSampleX,
                SubSampleY = cam.GetInfo().SubSampleY,
                SubSampleWidth = cam.GetInfo().SubSampleWidth,
                SubSampleHeight = cam.GetInfo().SubSampleHeight,
                ExposureMax = cam.GetInfo().ExposureMax,
                ExposureMin = cam.GetInfo().ExposureMin,
                LiveViewEnabled = cam.GetInfo().LiveViewEnabled,
                CanShowLiveView = cam.GetInfo().CanShowLiveView,
                SupportedActions = cam.GetInfo().SupportedActions,
                CanSetUSBLimit = cam.GetInfo().CanSetUSBLimit,
                Name = cam.GetInfo().Name,
                DisplayName = cam.GetInfo().DisplayName,
                DeviceId = cam.GetInfo().DeviceId,
                BayerOffsetX = cam.GetInfo().BayerOffsetX,
                BayerOffsetY = cam.GetInfo().BayerOffsetY,
                BinningModes = cam.GetInfo().BinningModes,
                BitDepth = cam.GetInfo().BitDepth,
                CameraState = cam.GetInfo().CameraState,
                XSize = cam.GetInfo().XSize,
                YSize = cam.GetInfo().YSize,
                CanGetGain = cam.GetInfo().CanGetGain,
                CanSetOffset = cam.GetInfo().CanSetOffset,
                CanSubSample = cam.GetInfo().CanSubSample,
                Description = cam.GetInfo().Description,
                DewHeaterOn = cam.GetInfo().DewHeaterOn,
                DriverInfo = cam.GetInfo().DriverInfo,
                DriverVersion = cam.GetInfo().DriverVersion,
                ElectronsPerADU = cam.GetInfo().ElectronsPerADU,
                ExposureEndTime = cam.GetInfo().ExposureEndTime,
                LastDownloadTime = cam.GetInfo().LastDownloadTime,
                SensorType = cam.GetInfo().SensorType,
                Gain = cam.GetInfo().Gain,
                offsetMax = cam.GetInfo().offsetMax,
                offsetMin = cam.GetInfo().offsetMin,
                TempChangeRunning = cam.TempChangeRunning,
                TargetTemp = cam.TargetTemp,
                // Exact equality of temperature and set point almost never held, and it held mid-ramp. NINA's own
                // AtTargetTemp compares with the target set at connect, which /cool does not update.
                AtTargetTemp = cam.GetInfo().CoolerOn
                    && !cam.TempChangeRunning
                    && Math.Abs(cam.GetInfo().Temperature - cam.GetInfo().TemperatureSetPoint) <= 2,
            };
        }
        public double TargetTemp { get; set; }
        public bool AtTargetTemp { get; set; }
    }

    /// <summary>
    /// pins: the camera slot an endpoint works on. The /equipment/camera and /equipment/guidecamera routes share
    /// their handlers; the guide camera slot is missing on a host without one.
    /// </summary>
    internal sealed class CameraEndpoint
    {
        public static readonly CameraEndpoint Imaging = new("Camera", () => AdvancedAPI.Controls.Camera, p => p.CameraSettings);
        public static readonly CameraEndpoint Guide = new("Guide camera", () => AdvancedAPI.Controls.GuideCamera, p => p.GuideCameraSettings);

        private readonly Func<ICameraMediator> mediator;
        private readonly Func<IProfile, ICameraSettings> settings;

        private CameraEndpoint(string name, Func<ICameraMediator> mediator, Func<IProfile, ICameraSettings> settings)
        {
            Name = name;
            this.mediator = mediator;
            this.settings = settings;
        }

        public string Name { get; }
        public ICameraMediator Mediator => mediator();
        public ICameraSettings Settings => settings(AdvancedAPI.Controls.Profile.ActiveProfile);
        public CancellationTokenSource CoolToken { get; set; }
    }

    public class CameraWatcher : INinaWatcher, ICameraConsumer
    {
        private readonly Func<object, EventArgs, Task> CameraConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("CAMERA-CONNECTED");
        private readonly Func<object, EventArgs, Task> CameraDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("CAMERA-DISCONNECTED");
        private readonly Func<object, EventArgs, Task> CameraDownloadTimeoutHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("CAMERA-DOWNLOAD-TIMEOUT");

        public void Dispose()
        {
            AdvancedAPI.Controls.Camera.RemoveConsumer(this);
        }

        public void StartWatchers()
        {
            AdvancedAPI.Controls.Camera.Connected += CameraConnectedHandler;
            AdvancedAPI.Controls.Camera.Disconnected += CameraDisconnectedHandler;
            AdvancedAPI.Controls.Camera.DownloadTimeout += CameraDownloadTimeoutHandler;
            AdvancedAPI.Controls.Camera.RegisterConsumer(this);
            AdvancedAPI.Controls.ImageSaveMediator.ImageSaved += OnImageSaved;
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.Camera.Connected -= CameraConnectedHandler;
            AdvancedAPI.Controls.Camera.Disconnected -= CameraDisconnectedHandler;
            AdvancedAPI.Controls.Camera.DownloadTimeout -= CameraDownloadTimeoutHandler;
            AdvancedAPI.Controls.Camera.RemoveConsumer(this);
            AdvancedAPI.Controls.ImageSaveMediator.ImageSaved -= OnImageSaved;
        }

        public async void UpdateDeviceInfo(CameraInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("CAMERA");
        }

        private static void OnImageSaved(object sender, ImageSavedEventArgs e)
        {
            ControllerV2.lastCaptureStatistics = new Dictionary<string, object>()
            {
                { "Timestamp", DateTime.UtcNow },
                { "Stars", e.StarDetectionAnalysis?.DetectedStars ?? 0 },
                { "HFR", e.StarDetectionAnalysis?.HFR ?? 0 },
                { "HFRStDev", e.StarDetectionAnalysis?.HFRStDev ?? 0 },
                { "Median", e.Statistics?.Median ?? 0 },
                { "MedianAbsoluteDeviation", e.Statistics?.MedianAbsoluteDeviation ?? 0 },
                { "Mean", e.Statistics?.Mean ?? 0 },
                { "Max", e.Statistics?.Max ?? 0 },
                { "MaxOccurrences", e.Statistics?.MaxOccurrences ?? 0 },
                { "Min", e.Statistics?.Min ?? 0 },
                { "MinOccurrences", e.Statistics?.MinOccurrences ?? 0 },
                { "StDev", e.Statistics?.StDev ?? 0 },
                { "Width", e.Image?.PixelWidth ?? 0 },
                { "Height", e.Image?.PixelHeight ?? 0 },
                { "BitDepth", e.Statistics?.BitDepth ?? 0 },
                { "Gain", e.MetaData?.Camera.Gain ?? -1 },
                { "Offset", e.MetaData?.Camera.Offset ?? -1 },
            };
        }
    }

    public partial class ControllerV2
    {
        private static PlateSolveResult plateSolveResult;
        private static bool isBayered;
        private static Task CaptureTask;

        // a capture is checked and started under this lock, and holds the camera until its exposure is down: a second
        // start between the IsExposing check and the camera taking the exposure (another client, a double tap) started a
        // second exposure and replaced the first one's task. Its processing (PNG, stars, solve) may overlap the next one
        private static readonly object CaptureStartLock = new();
        private static bool captureExposing;
        internal static Dictionary<string, object> lastCaptureStatistics;

        [Route(HttpVerbs.Get, "/equipment/camera/info")]
        public void CameraInfo() => InfoFor(CameraEndpoint.Imaging);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/info")]
        public void GuideCameraInfo() => InfoFor(CameraEndpoint.Guide);

        private void InfoFor(CameraEndpoint endpoint)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;
                CameraInfoResponse info = CameraInfoResponse.FromCam(cam);
                response.Response = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        /// <summary>
        /// pins: true while something other than the API's own capture holds the guide camera's capture block, i.e. the
        /// guider. Aborting its exposure or changing binning or readout mode under it would break a guide frame.
        /// </summary>
        private static bool GuideCameraInUseByGuider(CameraEndpoint endpoint)
        {
            return endpoint == CameraEndpoint.Guide && !endpoint.Mediator.IsFreeToCapture(guideCameraCaptureOwner);
        }

        /// <summary>
        /// pins: true while the camera exposes. Binning or readout mode changed under a running exposure (e.g. a frame of a
        /// sequence) ruins the frame: the driver reads it out with the new geometry or mode.
        /// </summary>
        private static bool CameraExposing(CameraEndpoint endpoint)
        {
            return endpoint.Mediator.GetInfo().IsExposing;
        }

        /// <summary>pins: the device settings that break a running guide frame, refused by set-setting like set-binning and set-readout.</summary>
        private static readonly HashSet<string> SettingsThatBreakAGuideFrame = new(StringComparer.Ordinal) { "BinX", "BinY", "ReadoutMode" };

        [Route(HttpVerbs.Get, "/equipment/camera/set-readout")]
        public void CameraSetReadout([QueryField] short mode) => SetReadoutFor(CameraEndpoint.Imaging, mode);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/set-readout")]
        public void GuideCameraSetReadout([QueryField] short mode) => SetReadoutFor(CameraEndpoint.Guide, mode);

        private void SetReadoutFor(CameraEndpoint endpoint, short mode)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            if (GuideCameraInUseByGuider(endpoint))
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is in use by the guider", 409)));
                return;
            }

            if (CameraExposing(endpoint))
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is exposing", 409)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (mode >= 0 && mode < cam.GetInfo().ReadoutModes.Count())
                {
                    cam.SetReadoutMode(mode);
                    response.Response = "Readout mode updated";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Invalid readout mode", 400));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/set-readout/image")]
        public void CameraSetReadoutImage([QueryField] short mode) => SetReadoutImageFor(CameraEndpoint.Imaging, mode);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/set-readout/image")]
        public void GuideCameraSetReadoutImage([QueryField] short mode) => SetReadoutImageFor(CameraEndpoint.Guide, mode);

        private void SetReadoutImageFor(CameraEndpoint endpoint, short mode)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            if (GuideCameraInUseByGuider(endpoint))
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is in use by the guider", 409)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (mode >= 0 && mode < cam.GetInfo().ReadoutModes.Count())
                {
                    ((ICamera)cam.GetDevice()).ReadoutModeForNormalImages = mode;
                    response.Response = "Readout mode updated";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Invalid readout mode", 400));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/set-readout/snapshot")]
        public void CameraSetReadoutSnapshot([QueryField] short mode) => SetReadoutSnapshotFor(CameraEndpoint.Imaging, mode);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/set-readout/snapshot")]
        public void GuideCameraSetReadoutSnapshot([QueryField] short mode) => SetReadoutSnapshotFor(CameraEndpoint.Guide, mode);

        private void SetReadoutSnapshotFor(CameraEndpoint endpoint, short mode)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            if (GuideCameraInUseByGuider(endpoint))
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is in use by the guider", 409)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (mode >= 0 && mode < cam.GetInfo().ReadoutModes.Count())
                {
                    ((ICamera)cam.GetDevice()).ReadoutModeForSnapImages = mode;
                    response.Response = "Readout mode updated";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Invalid readout mode", 400));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/cool")]
        public void CameraCool([QueryField] double temperature, [QueryField] bool cancel, [QueryField] double minutes) => CoolFor(CameraEndpoint.Imaging, temperature, cancel, minutes);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/cool")]
        public void GuideCameraCool([QueryField] double temperature, [QueryField] bool cancel, [QueryField] double minutes) => CoolFor(CameraEndpoint.Guide, temperature, cancel, minutes);

        private void CoolFor(CameraEndpoint endpoint, double temperature, bool cancel, double minutes)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (!cam.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not connected", 409));
                }
                else if (!cam.GetInfo().CanSetTemperature)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} has no temperature control", 409));
                }
                else
                {
                    if (cancel)
                    {
                        endpoint.CoolToken?.Cancel();
                        response.Response = "Cooling canceled";
                    }
                    else
                    {
                        endpoint.CoolToken?.Cancel();
                        endpoint.CoolToken = new CancellationTokenSource();
                        cam.CoolCamera(temperature, TimeSpan.FromMinutes(minutes == -1 ? endpoint.Settings.CoolingDuration : minutes), AdvancedAPI.Controls.StatusMediator.GetStatus(), endpoint.CoolToken.Token);
                        response.Response = "Cooling started";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/warm")]
        public void CameraWarm([QueryField] bool cancel, [QueryField] double minutes) => WarmFor(CameraEndpoint.Imaging, cancel, minutes);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/warm")]
        public void GuideCameraWarm([QueryField] bool cancel, [QueryField] double minutes) => WarmFor(CameraEndpoint.Guide, cancel, minutes);

        private void WarmFor(CameraEndpoint endpoint, bool cancel, double minutes)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (!cam.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not connected", 409));
                }
                else if (!cam.GetInfo().CanSetTemperature)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} has no temperature control", 409));
                }
                else
                {
                    if (cancel)
                    {
                        endpoint.CoolToken?.Cancel();
                        response.Response = "Warming canceled";
                    }
                    else
                    {
                        endpoint.CoolToken?.Cancel();
                        endpoint.CoolToken = new CancellationTokenSource();
                        cam.WarmCamera(TimeSpan.FromMinutes(minutes == -1 ? endpoint.Settings.WarmingDuration : minutes), AdvancedAPI.Controls.StatusMediator.GetStatus(), endpoint.CoolToken.Token);
                        response.Response = "Warming started";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/abort-exposure")]
        public void AbortExposure() => AbortExposureFor(CameraEndpoint.Imaging);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/abort-exposure")]
        public void GuideAbortExposure() => AbortExposureFor(CameraEndpoint.Guide);

        private void AbortExposureFor(CameraEndpoint endpoint)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            if (GuideCameraInUseByGuider(endpoint))
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is in use by the guider", 409)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (!cam.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not connected", 409));
                }
                else if (cam.GetInfo().IsExposing)
                {
                    response.Response = "Exposure aborted";
                    cam.AbortExposure();
                }
                else
                {
                    response.Response = "Camera not exposing";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/dew-heater")]
        public void CameraDewHeater([QueryField] bool power) => DewHeaterFor(CameraEndpoint.Imaging, power);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/dew-heater")]
        public void GuideCameraDewHeater([QueryField] bool power) => DewHeaterFor(CameraEndpoint.Guide, power);

        private void DewHeaterFor(CameraEndpoint endpoint, bool power)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (!cam.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not connected", 409));
                }
                else if (!cam.GetInfo().HasDewHeater)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} has no dew heater", 409));
                }
                else
                {
                    cam.SetDewHeater(power);
                    response.Response = "Dew heater set";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/usb-limit")]
        public void CameraDewHeater([QueryField] int limit) => UsbLimitFor(CameraEndpoint.Imaging, limit);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/usb-limit")]
        public void GuideCameraDewHeater([QueryField] int limit) => UsbLimitFor(CameraEndpoint.Guide, limit);

        private void UsbLimitFor(CameraEndpoint endpoint, int limit)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (!cam.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not connected", 409));
                }
                else if (!cam.GetInfo().CanSetUSBLimit)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} can not set USB limit", 409));
                }
                else if (limit < cam.GetInfo().USBLimitMin || limit > cam.GetInfo().USBLimitMax)
                {
                    response = CoreUtility.CreateErrorTable(new Error("USB limit out of range", 409));
                }
                else
                {
                    cam.SetUSBLimit(limit);
                    response.Response = "USB limit set";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/set-binning")]
        public void CameraSetBinning([QueryField] string binning) => SetBinningFor(CameraEndpoint.Imaging, binning);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/set-binning")]
        public void GuideCameraSetBinning([QueryField] string binning) => SetBinningFor(CameraEndpoint.Guide, binning);

        private void SetBinningFor(CameraEndpoint endpoint, string binning)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            if (GuideCameraInUseByGuider(endpoint))
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is in use by the guider", 409)));
                return;
            }

            if (CameraExposing(endpoint))
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is exposing", 409)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                ICameraMediator cam = endpoint.Mediator;

                if (string.IsNullOrEmpty(binning))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Binning must be specified", 409));
                }
                else if (!cam.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not connected", 409));
                }
                else if (binning.Contains('x'))
                {
                    string[] parts = binning.Split('x');
                    if (int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                    {
                        BinningMode mode = cam.GetInfo().BinningModes.FirstOrDefault(b => b.X == x && b.Y == y, null);
                        if (mode == null)
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Invalid binning mode", 409));
                        }
                        else
                        {
                            cam.SetBinning(mode.X, mode.Y);
                            response.Response = "Binning set";
                        }
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid binning mode", 409));
                    }
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Binning must be specified", 409));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/capture/statistics/full")]
        public void CameraCaptureFullStats()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (lastCaptureStatistics == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No capture processed", 400));
                }
                else
                {
                    response.Response = lastCaptureStatistics;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/capture/statistics")]
        public async Task CameraCaptureStats()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), $"temp.png")))
                {
                    response = CoreUtility.CreateErrorTable(new Error("No image available", 400));
                }
                else
                {
                    IImageData imageData = await Retry.Do(async () => await AdvancedAPI.Controls.ImageDataFactory.CreateFromFile(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), $"temp.png"), 16, isBayered, RawConverterEnum.FREEIMAGE), TimeSpan.FromMilliseconds(200), 10);
                    var img = await imageData.RenderImage().DetectStars(false, StarSensitivityEnum.Normal, NoiseReductionEnum.None);
                    var s = ImageStatistics.Create(img.RawImageData);

                    Dictionary<string, object> stats = new Dictionary<string, object>() {
                        { "Stars", img.RawImageData.StarDetectionAnalysis.DetectedStars },
                        { "HFR", img.RawImageData.StarDetectionAnalysis.HFR },
                        { "HFRStDev", img.RawImageData.StarDetectionAnalysis.HFRStDev },
                        { "Median", s.Median },
                        { "MedianAbsoluteDeviation", s.MedianAbsoluteDeviation },
                        { "Mean", s.Mean },
                        { "Max", s.Max },
                        { "MaxOccurrences", s.MaxOccurrences },
                        { "Min", s.Min },
                        { "MinOccurrences", s.MinOccurrences },
                        { "StDev", s.StDev },
                        { "Width", img.RawImageData.Properties.Width },
                        { "Height", img.RawImageData.Properties.Height },
                        { "BitDepth", img.RawImageData.Properties.BitDepth },
                        { "Gain", img.RawImageData.Properties.Gain },
                        { "Offset", img.RawImageData.Properties.Offset },
                };

                    response.Response = stats;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/capture")]
        public async Task CameraCapture(
            [QueryField] bool solve,
            [QueryField] float duration,
            [QueryField] bool getResult,
            [QueryField] bool resize,
            [QueryField] int quality,
            [QueryField] string size,
            [QueryField] int gain,
            [QueryField] double scale,
            [QueryField] bool stream,
            [QueryField] bool omitImage,
            [QueryField] bool waitForResult,
            [QueryField] bool save,
            [QueryField] string targetName,
            [QueryField] bool onlyAwaitCaptureCompletion,
            [QueryField] bool onlySaveRaw,
            [QueryField] bool skipAutoStretch,
            [QueryField] string imageType)
        {

            HttpResponse response = new HttpResponse();
            ICameraMediator cam = AdvancedAPI.Controls.Camera;

            quality = Math.Clamp(quality, -1, 100);
            if (quality == 0)
                quality = -1; // quality should be set to -1 for png if omitted

            if (resize && string.IsNullOrWhiteSpace(size)) // default value for size is 640x480
                size = "640x480";

            Size resolution = Size.Empty;


            try
            {
                if (resize)
                {
                    string[] s = size.Split('x');
                    int width = int.Parse(s[0]);
                    int height = int.Parse(s[1]);
                    resolution = new Size(width, height);
                }

                // wants result, task is running => error
                // wants result, task is not running => result
                // wants capture, camera exposing => error
                var captureTask = CaptureTask;
                if (getResult && captureTask is null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No capture processed", 409));
                }
                else if (getResult && !captureTask.IsCompleted)
                {
                    response.Response = "Capture already in progress";
                }
                else if (getResult && !captureTask.IsCompletedSuccessfully)
                {
                    // a failed capture leaves the previous capture's temp.png behind: it was served as this capture's image
                    response = CoreUtility.CreateErrorTable(new Error($"Capture failed: {captureTask.Exception?.GetBaseException().Message ?? "cancelled"}", 500));
                }
                else if (getResult && !stream && omitImage)
                {
                    // only the solve result: the full resolution PNG was decoded for every such request (clients poll it)
                    response.Response = new CaptureResponse() { PlateSolveResult = plateSolveResult, Image = null };
                }
                else if (getResult)
                {
                    BitmapSource source;
                    using (Bitmap img = new Bitmap(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), $"temp.png")))
                    {
                        source = ImageUtility.ConvertBitmap(img);
                    }

                    if (stream)
                    {
                        BitmapEncoder encoder = null;
                        if (scale == 0 && resize)
                        {
                            BitmapSource image = BitmapHelper.ResizeBitmap(source, resolution);
                            encoder = BitmapHelper.GetEncoder(image, quality);
                        }
                        if (scale != 0 && resize)
                        {
                            BitmapSource image = BitmapHelper.ScaleBitmap(source, scale);
                            encoder = BitmapHelper.GetEncoder(image, quality);
                        }
                        if (!resize)
                        {
                            BitmapSource image = BitmapHelper.ScaleBitmap(source, 1);
                            encoder = BitmapHelper.GetEncoder(image, quality);
                        }
                        HttpContext.Response.ContentType = quality == -1 ? "image/png" : "image/jpg";
                        using (MemoryStream memory = new MemoryStream())
                        {
                            encoder.Save(memory);
                            await HttpContext.Response.OutputStream.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length));
                            return;
                        }
                    }
                    else
                    {
                        string image = string.Empty;
                        if (scale == 0 && resize)
                            image = BitmapHelper.ResizeAndConvertBitmap(source, resolution, quality);
                        if (scale != 0 && resize)
                            image = BitmapHelper.ScaleAndConvertBitmap(source, scale, quality);
                        if (!resize)
                            image = BitmapHelper.ScaleAndConvertBitmap(source, 1, quality);

                        response.Response = new CaptureResponse() { Image = image, PlateSolveResult = plateSolveResult };
                    }
                }
                else if (!getResult && !cam.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Camera not connected", 409));
                }
                else if (!getResult && (cam.GetInfo().IsExposing || !TakeCameraForCapture()))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Camera currently exposing", 409));
                }
                else
                {
                    var started = Task.Run(async () =>
                    {
                        IPlateSolveSettings settings;
                        IExposureData exposure;
                        try
                        {
                            plateSolveResult = null;
                            lastCaptureStatistics = null;
                            settings = AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings;

                            CaptureSequence sequence = new CaptureSequence(
                                duration < 0 ? settings.ExposureTime : duration,
                                isImageTypeValid(imageType) ? imageType.ToUpper() : CaptureSequence.ImageTypes.SNAPSHOT,
                                AdvancedAPI.Controls.FilterWheel.GetInfo().SelectedFilter,
                                new BinningMode(cam.GetInfo().BinX, cam.GetInfo().BinY),
                                1);

                            if (!HttpContext.IsParameterOmitted(nameof(gain)) && gain >= 0)
                            {
                                sequence.Gain = gain;
                            }

                            exposure = await AdvancedAPI.Controls.Imaging.CaptureImage(
                                sequence,
                                CancellationToken.None,
                                AdvancedAPI.Controls.StatusMediator.GetStatus(),
                                string.IsNullOrEmpty(targetName) ? "Snapshot" : targetName
                            );
                        }
                        finally
                        {
                            lock (CaptureStartLock)
                            {
                                captureExposing = false;
                            }
                        }

                        PrepareImageParameters parameters = new PrepareImageParameters(autoStretch: !skipAutoStretch);
                        IRenderedImage renderedImage = await AdvancedAPI.Controls.Imaging.PrepareImage(exposure, parameters, CancellationToken.None);

                        if (!onlySaveRaw)
                        {
                            var encoder = BitmapHelper.GetEncoder(renderedImage.Image, -1);
                            using (FileStream fs = new FileStream(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), $"temp.png"), FileMode.Create))
                            {
                                encoder.Save(fs);
                            }
                        }

                        isBayered = renderedImage.RawImageData.Properties.IsBayered;

                        var detectedImage = await renderedImage.DetectStars(false, StarSensitivityEnum.Normal, NoiseReductionEnum.None);
                        var captureStats = ImageStatistics.Create(detectedImage.RawImageData);
                        lastCaptureStatistics = new Dictionary<string, object>()
                        {
                            { "Timestamp", DateTime.UtcNow },
                            { "Stars", detectedImage.RawImageData.StarDetectionAnalysis.DetectedStars },
                            { "HFR", detectedImage.RawImageData.StarDetectionAnalysis.HFR },
                            { "HFRStDev", detectedImage.RawImageData.StarDetectionAnalysis.HFRStDev },
                            { "Median", captureStats.Median },
                            { "MedianAbsoluteDeviation", captureStats.MedianAbsoluteDeviation },
                            { "Mean", captureStats.Mean },
                            { "Max", captureStats.Max },
                            { "MaxOccurrences", captureStats.MaxOccurrences },
                            { "Min", captureStats.Min },
                            { "MinOccurrences", captureStats.MinOccurrences },
                            { "StDev", captureStats.StDev },
                            { "Width", renderedImage.RawImageData.Properties.Width },
                            { "Height", renderedImage.RawImageData.Properties.Height },
                            { "BitDepth", renderedImage.RawImageData.Properties.BitDepth },
                            { "Gain", renderedImage.RawImageData.Properties.Gain },
                            { "Offset", renderedImage.RawImageData.Properties.Offset },
                        };

                        if (solve)
                        {
                            IPlateSolverFactory platesolver = AdvancedAPI.Controls.PlateSolver;
                            Coordinates coordinates = AdvancedAPI.Controls.Mount.GetCurrentPosition();
                            double focalLength = AdvancedAPI.Controls.Profile.ActiveProfile.TelescopeSettings.FocalLength;
                            CaptureSolverParameter solverParameter = new CaptureSolverParameter()
                            {
                                Attempts = 1,
                                // the binning the image was taken with (the camera's), not the plate solve profile's: with
                                // the camera on 2x2 and the profile on 1x1 the solver got half the image scale, and the
                                // solve failed or fell back to a slow blind solve
                                Binning = exposure.MetaData.Camera.BinX,
                                BlindFailoverEnabled = settings.BlindFailoverEnabled,
                                Coordinates = coordinates,
                                DownSampleFactor = settings.DownSampleFactor,
                                FocalLength = focalLength,
                                MaxObjects = settings.MaxObjects,
                                Regions = settings.Regions,
                                SearchRadius = settings.SearchRadius,
                                PixelSize = exposure.MetaData.Camera.PixelSize
                            };
                            IImageSolver captureSolver = platesolver.GetImageSolver(platesolver.GetPlateSolver(settings), platesolver.GetBlindSolver(settings));

                            plateSolveResult = await captureSolver.Solve(renderedImage.RawImageData, solverParameter, AdvancedAPI.Controls.StatusMediator.GetStatus(), CancellationToken.None);
                        }
                        if (save || onlySaveRaw)
                        {
                            await AdvancedAPI.Controls.ImageSaveMediator.Enqueue(renderedImage.RawImageData, Task.Run(() => renderedImage), AdvancedAPI.Controls.StatusMediator.GetStatus(), CancellationToken.None);
                        }
                        await WebSocketV2.SendAndAddEvent("API-CAPTURE-FINISHED");
                    }, CancellationToken.None);
                    CaptureTask = started;

                    response.Response = "Capture started";

                    if (onlyAwaitCaptureCompletion)
                    {
                        while (!started.IsCompleted && !cam.GetInfo().IsExposing)
                        {
                            await Task.Delay(10);
                        }
                        while (!started.IsCompleted && cam.GetInfo().IsExposing)
                        {
                            await Task.Delay(10);
                        }
                        response.Response = "Capture finished";
                    }

                    if (waitForResult)
                    {
                        try
                        {
                            await started;
                        }
                        catch (Exception ex)
                        {
                            // the catch-all below answered a failed capture with "Unknown error" instead of the reason
                            Logger.Error(ex);
                            response = CoreUtility.CreateErrorTable(new Error($"Capture failed: {ex.Message}", 500));
                        }
                    }

                    if (waitForResult && started.IsCompletedSuccessfully)
                    {
                        // Return the captured image
                        await CameraCapture(false, 0, true, resize, quality, size, 0, scale, stream, omitImage, false, false, targetName, false, onlySaveRaw, skipAutoStretch, imageType);
                        return;
                    }

                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        private static bool TakeCameraForCapture()
        {
            lock (CaptureStartLock)
            {
                if (captureExposing)
                {
                    return false;
                }

                captureExposing = true;
                return true;
            }
        }

        private bool isImageTypeValid(string imageType)
        {
            if (string.IsNullOrEmpty(imageType)) return false;
            imageType = imageType.ToUpper();
            if (imageType.Equals(CaptureSequence.ImageTypes.BIAS) ||
                imageType.Equals(CaptureSequence.ImageTypes.DARK) ||
                imageType.Equals(CaptureSequence.ImageTypes.FLAT) ||
                imageType.Equals(CaptureSequence.ImageTypes.LIGHT) ||
                imageType.Equals(CaptureSequence.ImageTypes.SNAPSHOT))
            {
                return true;
            }

            return false;
        }

        [Route(HttpVerbs.Get, "/equipment/camera/get-settings")]
        public void CameraGetSettings() => GetSettingsFor(CameraEndpoint.Imaging);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/get-settings")]
        public void GuideCameraGetSettings() => GetSettingsFor(CameraEndpoint.Guide);

        private void GetSettingsFor(CameraEndpoint endpoint)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                var device = endpoint.Mediator.GetDevice();
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} device not available", 409));
                }
                else
                {
                    var properties = device.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var settings = new Dictionary<string, object>();
                    foreach (var prop in properties)
                    {
                        if (prop.CanRead && prop.PropertyType != typeof(object))
                        {
                            try
                            {
                                settings[prop.Name] = prop.GetValue(device);
                            }
                            catch { }
                        }
                    }
                    response.Response = settings;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/camera/set-setting")]
        public void CameraSetSetting([QueryField] string settingName, [QueryField] string newValue) => SetSettingFor(CameraEndpoint.Imaging, settingName, newValue);

        [Route(HttpVerbs.Get, "/equipment/guidecamera/set-setting")]
        public void GuideCameraSetSetting([QueryField] string settingName, [QueryField] string newValue) => SetSettingFor(CameraEndpoint.Guide, settingName, newValue);

        private void SetSettingFor(CameraEndpoint endpoint, string settingName, string newValue)
        {
            if (endpoint.Mediator == null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} not available", 404)));
                return;
            }

            HttpResponse response = new HttpResponse();

            try
            {
                if (string.IsNullOrEmpty(settingName))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Invalid setting name", 400));
                }
                else if (string.IsNullOrEmpty(newValue))
                {
                    response = CoreUtility.CreateErrorTable(new Error("New value can't be null", 400));
                }
                else
                {
                    var device = endpoint.Mediator.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} device not available", 409));
                    }
                    else
                    {
                        var prop = device.GetType().GetProperty(settingName);
                        if (prop == null)
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Setting '{settingName}' not found", 400));
                        }
                        else if (SettingsThatBreakAGuideFrame.Contains(prop.Name) && GuideCameraInUseByGuider(endpoint))
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is in use by the guider", 409));
                        }
                        else if (SettingsThatBreakAGuideFrame.Contains(prop.Name) && CameraExposing(endpoint))
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"{endpoint.Name} is exposing", 409));
                        }
                        else
                        {
                            prop.SetValue(device, newValue.ConvertString(prop.PropertyType));
                            response.Response = "Setting updated";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }
    }
}
