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
using EmbedIO.WebSockets;
using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.WPF.Base.Mediator;
using NINA.WPF.Base.ViewModel.Equipment.Telescope;
using ninaAPI.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class MountInfo : TelescopeInfo
    {
        public TrackingMode? TrackingMode { get; set; }
        public MountInfo(ITelescopeMediator t)
        {
            var info = t.GetInfo();
            var props = info.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var thisType = typeof(MountInfo);
            foreach (var prop in props)
            {
                thisType.GetProperty(prop.Name).SetValue(this, prop.GetValue(info));
            }

            TrackingMode = (t.GetDevice() as ITelescope)?.TrackingMode;
        }
    }

    public class MountWatcher : INinaWatcher, ITelescopeConsumer
    {
        private readonly Func<object, EventArgs, Task> MountConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("MOUNT-CONNECTED");
        private readonly Func<object, EventArgs, Task> MountDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("MOUNT-DISCONNECTED");
        private readonly Func<object, BeforeMeridianFlipEventArgs, Task> MountBeforeMeridianFlipHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("MOUNT-BEFORE-FLIP");
        private readonly Func<object, AfterMeridianFlipEventArgs, Task> MountAfterMeridianFlipHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("MOUNT-AFTER-FLIP");
        private readonly Func<object, EventArgs, Task> MountHomedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("MOUNT-HOMED");
        private readonly Func<object, EventArgs, Task> MountParkedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("MOUNT-PARKED");
        private readonly Func<object, EventArgs, Task> MountUnparkedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("MOUNT-UNPARKED");
        // Every goto issued through NINA, centering slews included. MOUNT-SLEW-START/STOP only see a slew an info poll
        // catches, which misses one that ends between two polls and all of them on a mount that doesn't report Slewing (OnStep)
        private readonly Func<object, MountSlewedEventArgs, Task> MountSlewedHandler = async (_, e) => await WebSocketV2.SendAndAddEvent("MOUNT-SLEWED", new Dictionary<string, object>() {
            { "From", SlewCoordinates(e.From) },
            { "To", SlewCoordinates(e.To) }
        });

        // The fields of the websocket spec with the epoch as its name, instead of the whole Coordinates object
        private static Dictionary<string, object> SlewCoordinates(Coordinates c) => c == null ? null : new Dictionary<string, object>() {
            { "RA", c.RA },
            { "RADegrees", c.RADegrees },
            { "RAString", c.RAString },
            { "Dec", c.Dec },
            { "DecString", c.DecString },
            { "Epoch", c.Epoch.ToString() }
        };

        public void Dispose()
        {
            AdvancedAPI.Controls.Mount.RemoveConsumer(this);
        }

        public void StartWatchers()
        {
            AdvancedAPI.Controls.Mount.Connected += MountConnectedHandler;
            AdvancedAPI.Controls.Mount.Disconnected += MountDisconnectedHandler;
            AdvancedAPI.Controls.Mount.BeforeMeridianFlip += MountBeforeMeridianFlipHandler;
            AdvancedAPI.Controls.Mount.AfterMeridianFlip += MountAfterMeridianFlipHandler;
            AdvancedAPI.Controls.Mount.Homed += MountHomedHandler;
            AdvancedAPI.Controls.Mount.Parked += MountParkedHandler;
            AdvancedAPI.Controls.Mount.Unparked += MountUnparkedHandler;
            AdvancedAPI.Controls.Mount.Slewed += MountSlewedHandler;
            AdvancedAPI.Controls.Mount.RegisterConsumer(this);
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.Mount.Connected -= MountConnectedHandler;
            AdvancedAPI.Controls.Mount.Disconnected -= MountDisconnectedHandler;
            AdvancedAPI.Controls.Mount.BeforeMeridianFlip -= MountBeforeMeridianFlipHandler;
            AdvancedAPI.Controls.Mount.AfterMeridianFlip -= MountAfterMeridianFlipHandler;
            AdvancedAPI.Controls.Mount.Homed -= MountHomedHandler;
            AdvancedAPI.Controls.Mount.Parked -= MountParkedHandler;
            AdvancedAPI.Controls.Mount.Unparked -= MountUnparkedHandler;
            AdvancedAPI.Controls.Mount.Slewed -= MountSlewedHandler;
            AdvancedAPI.Controls.Mount.RemoveConsumer(this);
        }

        // Null until the first update after connect, so an already tracking mount still gets its start event
        private static bool? lastSlewing;
        private static bool? lastTracking;

        public async void UpdateDeviceInfo(TelescopeInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("MOUNT");

            if (deviceInfo == null || !deviceInfo.Connected)
            {
                lastSlewing = null;
                lastTracking = null;
                return;
            }

            // Remember the new values before awaiting: the next update may arrive during a send
            bool slewing = deviceInfo.Slewing;
            bool tracking = deviceInfo.TrackingEnabled;
            bool slewingChanged = slewing != (lastSlewing ?? false);
            bool trackingChanged = tracking != (lastTracking ?? false);
            lastSlewing = slewing;
            lastTracking = tracking;

            if (slewingChanged)
            {
                await WebSocketV2.SendAndAddEvent(slewing ? "MOUNT-SLEW-START" : "MOUNT-SLEW-STOP");
            }
            if (trackingChanged)
            {
                await WebSocketV2.SendAndAddEvent(tracking ? "MOUNT-TRACKING-START" : "MOUNT-TRACKING-STOP");
            }
        }
    }

    public partial class ControllerV2
    {
        [Route(HttpVerbs.Get, "/equipment/mount/info")]
        public void MountInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;
                response.Response = new MountInfo(mount);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/mount/home")]
        public void MountHome()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 409));
                }
                else if (mount.GetInfo().AtPark)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount parked", 409));
                }
                else if (mount.GetInfo().AtHome)
                {
                    response.Response = "Mount already homed";
                }
                else
                {
                    if (mount.GetInfo().Slewing)
                    {
                        mount.StopSlew();
                    }
                    BackgroundOperation.Report(mount.FindHome(AdvancedAPI.Controls.StatusMediator.GetStatus(), new CancellationTokenSource().Token), "MOUNT-HOME-FAILED", "Mount homing");
                    response.Response = "Homing";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/mount/tracking")]
        public void MountTrackingMode([QueryField] int mode)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 409));
                }
                else if (mount.GetInfo().AtPark)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount parked", 409));
                }
                else
                {
                    // Siderial: 0
                    // Lunar: 1
                    // Solar: 2
                    // King: 3
                    // Stopped: 4 (but actually 5)
                    if (mode == 4)
                        mode++;

                    if (mode >= 0 && mode < 6)
                    {
                        response.Response = "Tracking mode changed";
                        response.Success = mount.SetTrackingMode((TrackingMode)mode);
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid tracking mode", 400));
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

        [Route(HttpVerbs.Get, "/equipment/mount/park")]
        public void MountPark()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 409));
                }
                else if (mount.GetInfo().AtPark)
                {
                    response.Response = "Mount already parked";
                }
                else
                {
                    if (mount.GetInfo().Slewing)
                    {
                        mount.StopSlew();
                    }
                    BackgroundOperation.Report(mount.ParkTelescope(AdvancedAPI.Controls.StatusMediator.GetStatus(), new CancellationTokenSource().Token), "MOUNT-PARK-FAILED", "Mount park");
                    response.Response = "Parking";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/mount/unpark")]
        public void MountUnpark()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 409));
                }
                else if (!mount.GetInfo().AtPark)
                {
                    response.Response = "Mount not parked";
                }
                else
                {
                    BackgroundOperation.Report(mount.UnparkTelescope(AdvancedAPI.Controls.StatusMediator.GetStatus(), new CancellationTokenSource().Token), "MOUNT-UNPARK-FAILED", "Mount unpark");
                    response.Response = "Unparking";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/mount/flip")]
        public void MountFlip()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 409));
                }
                else if (mount.GetInfo().AtPark)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount parked", 409));
                }
                else
                {
                    BackgroundOperation.Report(mount.MeridianFlip(mount.GetInfo().Coordinates, new CancellationTokenSource().Token), "MOUNT-FLIP-FAILED", "Mount meridian flip");
                    response.Response = "Flipping";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/mount/slew")]
        public async Task MountSlew([QueryField] double ra, [QueryField] double dec, [QueryField] bool waitForResult, [QueryField] bool center, [QueryField] bool rotate, [QueryField] double rotationAngle)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 409));
                }
                else if (mount.GetInfo().AtPark)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount parked", 409));
                }
                else
                {
                    if (rotate)
                    {
                        CenterAndRotate cr = new CenterAndRotate(AdvancedAPI.Controls.Profile, mount, AdvancedAPI.Controls.Imaging, AdvancedAPI.Controls.Rotator, AdvancedAPI.Controls.FilterWheel, AdvancedAPI.Controls.Guider, AdvancedAPI.Controls.Dome, AdvancedAPI.Controls.DomeFollower, AdvancedAPI.Controls.PlateSolver, AdvancedAPI.Controls.WindowFactory);
                        cr.Coordinates.Coordinates = new Coordinates(Angle.ByDegree(ra), Angle.ByDegree(dec), Epoch.J2000);
                        cr.PositionAngle = rotationAngle;

                        SlewCenterToken?.Cancel();
                        SlewCenterToken = new CancellationTokenSource();
                        if (waitForResult)
                        {
                            await cr.Execute(AdvancedAPI.Controls.StatusMediator.GetStatus(), SlewCenterToken.Token);
                            response.Success = cr.Status == SequenceEntityStatus.FINISHED;
                            response.Response = response.Success ? "Slew finished" : "Slew failed";
                        }
                        else
                        {
                            cr.Execute(AdvancedAPI.Controls.StatusMediator.GetStatus(), SlewCenterToken.Token);
                            response.Response = "Slew started";
                        }
                    }
                    else if (center)
                    {
                        Center instruction = new Center(AdvancedAPI.Controls.Profile, mount, AdvancedAPI.Controls.Imaging, AdvancedAPI.Controls.FilterWheel, AdvancedAPI.Controls.Guider, AdvancedAPI.Controls.Dome, AdvancedAPI.Controls.DomeFollower, AdvancedAPI.Controls.PlateSolver, AdvancedAPI.Controls.WindowFactory);
                        instruction.Coordinates.Coordinates = new Coordinates(Angle.ByDegree(ra), Angle.ByDegree(dec), Epoch.J2000);

                        SlewCenterToken?.Cancel();
                        SlewCenterToken = new CancellationTokenSource();
                        Logger.Info(JsonConvert.SerializeObject(instruction.Coordinates.Coordinates));
                        if (waitForResult)
                        {
                            await instruction.Execute(AdvancedAPI.Controls.StatusMediator.GetStatus(), SlewCenterToken.Token);
                            response.Success = instruction.Status == SequenceEntityStatus.FINISHED;
                            response.Response = response.Success ? "Slew finished" : "Slew failed";
                        }
                        else
                        {
                            instruction.Execute(AdvancedAPI.Controls.StatusMediator.GetStatus(), SlewCenterToken.Token);
                            response.Response = "Slew started";
                        }
                    }
                    else
                    {
                        if (waitForResult)
                        {
                            try
                            {
                                bool result = await mount.SlewToCoordinatesAsync(new Coordinates(Angle.ByDegree(ra), Angle.ByDegree(dec), Epoch.J2000), CancellationToken.None);
                                response.Success = result;
                                response.Response = result ? "Slew finished" : "Slew failed";
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // pins' INDI and OnStepX mounts throw on a refused or timed out slew; the catch-all
                                // below answered that with "Unknown error" instead of the reason
                                Logger.Error(ex);
                                response.Success = false;
                                response.Response = "Slew failed";
                                response.Error = ex.Message;
                            }
                        }
                        else
                        {
                            mount.SlewToCoordinatesAsync(new Coordinates(Angle.ByDegree(ra), Angle.ByDegree(dec), Epoch.J2000), CancellationToken.None);
                            response.Response = "Slew started";
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

        private static CancellationTokenSource SlewCenterToken;

        [Route(HttpVerbs.Get, "/equipment/mount/slew/stop")]
        public void MountStopSlew()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 409));
                }
                else
                {
                    mount.StopSlew();
                    SlewCenterToken?.Cancel();
                    response.Response = "Stopped slew";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/mount/set-park-position")]
        public async Task MountSetPark()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 400));
                }
                else if (!mount.GetInfo().CanSetPark || mount.GetInfo().AtPark)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount can not set park position", 400));
                }
                else
                {
                    var vm = typeof(TelescopeMediator).GetField("handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(mount) as TelescopeVM;
                    bool result = await vm.SetParkPosition();
                    response.Success = result;
                    response.Response = result ? "Park position set" : "";
                    response.Error = result ? "" : "Park position update failed";
                    response.StatusCode = result ? 200 : 400;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/mount/sync")]
        public async Task MountSync([QueryField] double ra, [QueryField] double dec)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 400));
                }
                else if (mount.GetInfo().AtPark)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount is parked", 400));
                }
                else
                {
                    if (!HttpContext.IsParameterOmitted(nameof(ra)) && !HttpContext.IsParameterOmitted(nameof(dec)))
                    {
                        await mount.Sync(new Coordinates(Angle.ByDegree(ra), Angle.ByDegree(dec), Epoch.J2000));
                        response.Response = "Synced";
                    }
                    else
                    {
                        SolveAndSync instruction = new SolveAndSync(
                            AdvancedAPI.Controls.Profile,
                            mount,
                            AdvancedAPI.Controls.Rotator,
                            AdvancedAPI.Controls.Imaging,
                            AdvancedAPI.Controls.FilterWheel,
                            AdvancedAPI.Controls.PlateSolver,
                            AdvancedAPI.Controls.WindowFactory);
                        await instruction.Run(AdvancedAPI.Controls.StatusMediator.GetStatus(), CancellationToken.None);
                        response.Response = "Synced";
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

        [Route(HttpVerbs.Get, "/equipment/mount/get-settings")]
        public void MountGetSettings()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.Mount.GetDevice();
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount device not available", 409));
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

        [Route(HttpVerbs.Get, "/equipment/mount/set-setting")]
        public void MountSetSetting([QueryField] string settingName, [QueryField] string newValue)
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
                    var device = AdvancedAPI.Controls.Mount.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Mount device not available", 409));
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

    public class MountAxisMoveSocket : WebSocketModule
    {
        private static DateTime eastTimer;
        private double eastRate;
        private static DateTime westTimer;
        private double westRate;
        private static DateTime northTimer;
        private double northRate;
        private static DateTime southTimer;
        private double southRate;

        private static object _timerLock = new object();

        public MountAxisMoveSocket(string urlPath) : base(urlPath, true)
        {

        }

        protected override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
        {
            HttpResponse response = new HttpResponse();
            response.Type = HttpResponse.TypeSocket;
            try
            {
                var message = Encoding.GetString(buffer);
                var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(message);
                string direction = json["direction"].ToString().ToLower();
                double rate = double.Parse(json["rate"].ToString());

                ITelescopeMediator mount = AdvancedAPI.Controls.Mount;

                if (!mount.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount not connected", 400));
                }
                else if (mount.GetInfo().AtPark)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Mount parked", 400));
                }
                else
                {
                    switch (direction)
                    {
                        case "east":
                            if (eastRate != rate)
                            {
                                mount.MoveAxis(TelescopeAxes.Primary, rate);
                            }
                            lock (_timerLock)
                            {
                                eastTimer = DateTime.Now;
                                eastRate = rate;
                            }
                            DelayedAction.Execute(TimeSpan.FromMilliseconds(2000), () =>
                            {
                                lock (_timerLock)
                                {
                                    Logger.Debug($"Time since last message: {DateTime.Now - eastTimer}");
                                    if (DateTime.Now - eastTimer > TimeSpan.FromSeconds(1.8)) // This difference is due to the inaccuracy of the cpu scheduler
                                    {
                                        mount.MoveAxis(TelescopeAxes.Primary, 0);
                                        eastRate = 0;
                                    }
                                }
                            });
                            break;

                        case "west":
                            if (westRate != rate)
                            {
                                mount.MoveAxis(TelescopeAxes.Primary, -rate);
                            }
                            lock (_timerLock)
                            {
                                westTimer = DateTime.Now;
                                westRate = rate;
                            }
                            DelayedAction.Execute(TimeSpan.FromMilliseconds(2000), () =>
                            {
                                lock (_timerLock)
                                {
                                    Logger.Debug($"Time since last message: {DateTime.Now - westTimer}");
                                    if (DateTime.Now - westTimer > TimeSpan.FromSeconds(1.8))
                                    {
                                        mount.MoveAxis(TelescopeAxes.Primary, 0);
                                        westRate = 0;
                                    }
                                }
                            });
                            break;

                        case "north":
                            if (northRate != rate)
                            {
                                mount.MoveAxis(TelescopeAxes.Secondary, rate);
                            }
                            lock (_timerLock)
                            {
                                northTimer = DateTime.Now;
                                northRate = rate;
                            }
                            DelayedAction.Execute(TimeSpan.FromMilliseconds(2000), () =>
                            {
                                lock (_timerLock)
                                {
                                    Logger.Debug($"Time since last message: {DateTime.Now - northTimer}");
                                    if (DateTime.Now - northTimer > TimeSpan.FromSeconds(1.8))
                                    {
                                        mount.MoveAxis(TelescopeAxes.Secondary, 0);
                                        northRate = 0;
                                    }
                                }
                            });
                            break;

                        case "south":
                            if (southRate != rate)
                            {
                                mount.MoveAxis(TelescopeAxes.Secondary, -rate);
                            }
                            lock (_timerLock)
                            {
                                southTimer = DateTime.Now;
                                southRate = rate;
                            }
                            DelayedAction.Execute(TimeSpan.FromMilliseconds(2000), () =>
                            {
                                lock (_timerLock)
                                {
                                    Logger.Debug($"Time since last message: {DateTime.Now - southTimer}");
                                    if (DateTime.Now - southTimer > TimeSpan.FromSeconds(1.8))
                                    {
                                        mount.MoveAxis(TelescopeAxes.Secondary, 0);
                                        southRate = 0;
                                    }
                                }
                            });
                            break;

                        default:
                            response = CoreUtility.CreateErrorTable(new Error("Invalid direction", 400));
                            break;
                    }
                    response.Response = rate == 0 ? "Stopped Move" : "Moving";
                }
            }
            catch (Exception ex)
            {
                response = CoreUtility.CreateErrorTable(new Error(ex.Message, 400));
            }
            await context.WebSocket.SendAsync(Encoding.GetBytes(System.Text.Json.JsonSerializer.Serialize(response)), true);
        }
    }
}
