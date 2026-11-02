#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Interfaces;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using ninaAPI.Utility;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class GuideInfo
    {
        public GuideInfo(GuiderInfo inf, GuideStep step, string state)
        {
            Connected = inf.Connected;
            Name = inf.Name;
            DisplayName = inf.DisplayName;
            Description = inf.Description;
            DriverInfo = inf.DriverInfo;
            DriverVersion = inf.DriverVersion;
            DeviceId = inf.DeviceId;
            CanClearCalibration = inf.CanClearCalibration;
            CanSetShiftRate = inf.CanSetShiftRate;
            CanGetLockPosition = inf.CanGetLockPosition;
            SupportedActions = inf.SupportedActions;
            RMSError = inf.RMSError;
            PixelScale = inf.PixelScale;
            LastGuideStep = step;

            State = state;
        }

        public bool Connected { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string DriverInfo { get; set; }
        public string DriverVersion { get; set; }
        public string DeviceId { get; set; }
        public bool CanClearCalibration { get; set; }

        public bool CanSetShiftRate { get; set; }

        public bool CanGetLockPosition { get; set; }

        public IList<string> SupportedActions { get; set; }

        public RMSError RMSError { get; set; }

        public double PixelScale { get; set; }
        public GuideStep LastGuideStep { get; set; }
        public string State { get; set; }
    }

    public class GuideStep
    {
        public double RADistanceRaw { get; set; }
        public double DECDistanceRaw { get; set; }

        public double RADuration { get; set; }
        public double DECDuration { get; set; }
    }

    public class GuideStepHistoryEntry
    {
        // Counts up by one per step, the cursor of /equipment/guider/history. Unlike Time it
        // cannot go back, e.g. when NTP or GPS sets the clock of a Pi without RTC mid-session.
        public long Id { get; set; }
        // UTC
        public DateTime Time { get; set; }
        public double RADistanceRaw { get; set; }
        public double DECDistanceRaw { get; set; }
        public double RADuration { get; set; }
        public double DECDuration { get; set; }
        // Only reported by PHD2
        public double? SNR { get; set; }
        public double? StarMass { get; set; }
        public double? HFD { get; set; }

        public static GuideStepHistoryEntry FromStep(IGuideStep step)
        {
            var phd = step as PhdEventGuideStep;
            return new GuideStepHistoryEntry()
            {
                Time = DateTime.UtcNow,
                RADistanceRaw = step.RADistanceRaw,
                DECDistanceRaw = step.DECDistanceRaw,
                RADuration = step.RADuration,
                DECDuration = step.DECDuration,
                SNR = phd?.SNR,
                StarMass = phd?.StarMass,
                HFD = phd?.HFD,
            };
        }
    }

    public class GuideStepHistoryResponse
    {
        public string Session { get; set; }
        public double PixelScale { get; set; }
        public int Count { get; set; }
        public int MaxSize { get; set; }
        public List<GuideStepHistoryEntry> Steps { get; set; }
    }

    public class GuiderWatcher : INinaWatcher, IGuiderConsumer
    {
        public static GuideStep lastGuideStep { get; set; }

        // Every guide step since the API started; beyond this the oldest are dropped (~14 h at 1 step/s).
        public const int MaxHistorySize = 50000;
        // A new value whenever NINA starts, which starts the step ids over
        public static readonly string HistorySession = Guid.NewGuid().ToString();
        private static readonly object historyLock = new object();
        private static readonly List<GuideStepHistoryEntry> history = new List<GuideStepHistoryEntry>();
        private static long lastHistoryId;

        private static INotifyPropertyChanged observedGuider;
        private static string lastState;

        private static void AddHistoryEntry(GuideStepHistoryEntry entry)
        {
            lock (historyLock)
            {
                entry.Id = ++lastHistoryId;
                history.Add(entry);
                if (history.Count > MaxHistorySize)
                {
                    history.RemoveRange(0, history.Count - MaxHistorySize);
                }
            }
        }

        public static List<GuideStepHistoryEntry> GetHistory(long afterId, out int total)
        {
            lock (historyLock)
            {
                total = history.Count;
                // The ids are consecutive, so the first entry after the cursor is at a known offset
                int start = history.Count == 0 ? 0 : (int)Math.Clamp(afterId - history[0].Id + 1, 0, history.Count);
                return history.GetRange(start, history.Count - start);
            }
        }

        // The guider state (PHD2 app state) is only observable on the device itself:
        // neither GuiderInfo nor the mediator events report Paused, LostLock or Stopped.
        private static void ObserveGuider()
        {
            var device = AdvancedAPI.Controls.Guider.GetDevice() as INotifyPropertyChanged;
            if (ReferenceEquals(device, observedGuider))
            {
                return;
            }
            if (observedGuider != null)
            {
                observedGuider.PropertyChanged -= GuiderPropertyChanged;
            }
            observedGuider = device;
            lastState = null;
            if (device != null)
            {
                device.PropertyChanged += GuiderPropertyChanged;
                GuiderPropertyChanged(device, new PropertyChangedEventArgs(nameof(IGuider.State)));
            }
        }

        // async void: an exception escaping it ends the process when there is no synchronization
        // context (headless pins), so it logs instead.
        private static async void GuiderPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName != nameof(IGuider.State))
                {
                    return;
                }
                string state = (sender as IGuider)?.State;
                if (string.IsNullOrEmpty(state) || state == lastState)
                {
                    return;
                }
                lastState = state;
                await WebSocketV2.SendAndAddEvent("GUIDER-STATE", new Dictionary<string, object>() { { "State", state } });
            }
            catch (Exception ex)
            {
                Logger.Error("GUIDER-STATE event failed", ex);
            }
        }

        private readonly Func<object, EventArgs, Task> GuiderConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-CONNECTED");
        private readonly Func<object, EventArgs, Task> GuiderDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-DISCONNECTED");
        private readonly Func<object, EventArgs, Task> GuiderDitherHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-DITHER");
        private readonly Func<object, EventArgs, Task> GuiderStartHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-START");
        private readonly Func<object, EventArgs, Task> GuiderStopHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-STOP");
        private readonly EventHandler<IGuideStep> GuiderGuideEventHandler = (object sender, IGuideStep e) =>
        {
            lastGuideStep = new GuideStep() { DECDistanceRaw = e.DECDistanceRaw, DECDuration = e.DECDuration, RADistanceRaw = e.RADistanceRaw, RADuration = e.RADuration };
            AddHistoryEntry(GuideStepHistoryEntry.FromStep(e));
        };

        public void StartWatchers()
        {
            AdvancedAPI.Controls.Guider.GuideEvent += GuiderGuideEventHandler;
            AdvancedAPI.Controls.Guider.Connected += GuiderConnectedHandler;
            AdvancedAPI.Controls.Guider.Disconnected += GuiderDisconnectedHandler;
            AdvancedAPI.Controls.Guider.AfterDither += GuiderDitherHandler;
            AdvancedAPI.Controls.Guider.GuidingStarted += GuiderStartHandler;
            AdvancedAPI.Controls.Guider.GuidingStopped += GuiderStopHandler;
            AdvancedAPI.Controls.Guider.RegisterConsumer(this);
            ObserveGuider();
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.Guider.GuideEvent -= GuiderGuideEventHandler;
            AdvancedAPI.Controls.Guider.Connected -= GuiderConnectedHandler;
            AdvancedAPI.Controls.Guider.Disconnected -= GuiderDisconnectedHandler;
            AdvancedAPI.Controls.Guider.AfterDither -= GuiderDitherHandler;
            AdvancedAPI.Controls.Guider.GuidingStarted -= GuiderStartHandler;
            AdvancedAPI.Controls.Guider.GuidingStopped -= GuiderStopHandler;
            AdvancedAPI.Controls.Guider.RemoveConsumer(this);
            if (observedGuider != null)
            {
                observedGuider.PropertyChanged -= GuiderPropertyChanged;
                observedGuider = null;
            }
        }

        public async void UpdateDeviceInfo(GuiderInfo deviceInfo)
        {
            try
            {
                if (deviceInfo?.Connected != true)
                {
                    // A reconnect may bring back the same guider object in the same state,
                    // which is still news after the disconnect
                    lastState = null;
                }
                // Broadcast on connect and disconnect, which is when the device changes
                ObserveGuider();
                await WebSocketV2.SendConsumerEvent("GUIDER");
            }
            catch (Exception ex)
            {
                // async void, see GuiderPropertyChanged
                Logger.Error("Updating the observed guider failed", ex);
            }
        }

        public void Dispose()
        {
            AdvancedAPI.Controls.Guider.RemoveConsumer(this);
        }
    }

    public partial class ControllerV2
    {
        private static CancellationTokenSource GuideToken;

        [Route(HttpVerbs.Get, "/equipment/guider/info")]
        public void GuiderInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                IGuider g = (IGuider)guider.GetDevice();

                GuideInfo info = new GuideInfo(guider.GetInfo(), GuiderWatcher.lastGuideStep, g?.State);
                response.Response = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/start")]
        public async Task GuiderStart([QueryField] bool calibrate)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                if (guider.GetInfo().Connected)
                {
                    GuideToken?.Cancel();
                    GuideToken = new CancellationTokenSource();
                    // StartGuiding answers false when guiding could not be started (e.g. a failed calibration): the
                    // answer was "Guiding started" all the same
                    if (await guider.StartGuiding(calibrate, AdvancedAPI.Controls.StatusMediator.GetStatus(), GuideToken.Token))
                    {
                        response.Response = "Guiding started";
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Guiding could not be started", 500));
                    }
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider not connected", 409));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/stop")]
        public async Task GuiderStop()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                if (guider.GetInfo().Connected)
                {
                    await guider.StopGuiding(CancellationToken.None);
                    response.Response = "Guiding stopped";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider not connected", 409));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/clear-calibration")]
        public async Task ClearCalibration()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                if (guider.GetInfo().Connected)
                {
                    response.Success = await guider.ClearCalibration(CancellationToken.None);
                    response.Response = "Calibration cleared";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider not connected", 409));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/graph")]
        public void GuiderGraph()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                var handlerField = guider.GetType().GetField("handler",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.FlattenHierarchy);

                IGuiderVM gvm = (IGuiderVM)handlerField.GetValue(guider);
                var guiderProperty = gvm.GetType().GetProperty("GuideStepsHistory");

                GuideStepsHistory history = (GuideStepsHistory)guiderProperty.GetValue(gvm);
                response.Response = history;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/history")]
        public void GuiderHistory([QueryField] long after)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                List<GuideStepHistoryEntry> steps = GuiderWatcher.GetHistory(after, out int total);

                double pixelScale = 0;
                try
                {
                    pixelScale = AdvancedAPI.Controls.Guider.GetInfo().PixelScale;
                }
                catch (Exception) { }

                response.Response = new GuideStepHistoryResponse()
                {
                    Session = GuiderWatcher.HistorySession,
                    PixelScale = pixelScale,
                    Count = total,
                    MaxSize = GuiderWatcher.MaxHistorySize,
                    Steps = steps,
                };
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/graph/clear")]
        public void GuiderGraphClear()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                var handlerField = guider.GetType().GetField("handler",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.FlattenHierarchy);

                IGuiderVM gvm = (IGuiderVM)handlerField.GetValue(guider);
                var guiderProperty = gvm.GetType().GetProperty("GuideStepsHistory");

                GuideStepsHistory history = (GuideStepsHistory)guiderProperty.GetValue(gvm);
                history.Clear();
                response.Response = "Guide graph cleared";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/get-settings")]
        public void GuiderGetSettings()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.Guider.GetDevice();
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider device not available", 409));
                }
                else
                {
                    var properties = device.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var settings = new Dictionary<string, object>();
                    foreach (var prop in properties)
                    {
                        if (prop.CanRead)
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

        [Route(HttpVerbs.Get, "/equipment/guider/set-setting")]
        public void GuiderSetSetting([QueryField] string settingName, [QueryField] string newValue)
        {
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
                    var device = AdvancedAPI.Controls.Guider.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Guider device not available", 409));
                    }
                    else
                    {
                        var prop = device.GetType().GetProperty(settingName);
                        if (prop == null)
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Setting '{settingName}' not found", 400));
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
