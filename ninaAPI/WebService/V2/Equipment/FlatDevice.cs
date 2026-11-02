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
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Interfaces.Mediator;
using ninaAPI.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class FlatDeviceWatcher : INinaWatcher, IFlatDeviceConsumer
    {
        private readonly Func<object, EventArgs, Task> FlatDeviceConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("FLAT-CONNECTED");
        private readonly Func<object, EventArgs, Task> FlatDeviceDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("FLAT-DISCONNECTED");
        private readonly Func<object, EventArgs, Task> FlatDeviceLightToggledHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("FLAT-LIGHT-TOGGLED");
        private readonly Func<object, EventArgs, Task> FlatDeviceOpenedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("FLAT-COVER-OPENED");
        private readonly Func<object, EventArgs, Task> FlatDeviceClosedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("FLAT-COVER-CLOSED");
        private readonly Func<object, FlatDeviceBrightnessChangedEventArgs, Task> FlatDeviceBrightnessChangedHandler = async (_, e) => await WebSocketV2.SendAndAddEvent(
            "FLAT-BRIGHTNESS-CHANGED",
            new Dictionary<string, object>() { { "Previous", e.From }, { "New", e.To } });

        public void Dispose()
        {
            AdvancedAPI.Controls.FlatDevice.RemoveConsumer(this);
        }

        public void StartWatchers()
        {
            AdvancedAPI.Controls.FlatDevice.Connected += FlatDeviceConnectedHandler;
            AdvancedAPI.Controls.FlatDevice.Disconnected += FlatDeviceDisconnectedHandler;
            AdvancedAPI.Controls.FlatDevice.LightToggled += FlatDeviceLightToggledHandler;
            AdvancedAPI.Controls.FlatDevice.Opened += FlatDeviceOpenedHandler;
            AdvancedAPI.Controls.FlatDevice.Closed += FlatDeviceClosedHandler;
            AdvancedAPI.Controls.FlatDevice.BrightnessChanged += FlatDeviceBrightnessChangedHandler;
            AdvancedAPI.Controls.FlatDevice.RegisterConsumer(this);
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.FlatDevice.Connected -= FlatDeviceConnectedHandler;
            AdvancedAPI.Controls.FlatDevice.Disconnected -= FlatDeviceDisconnectedHandler;
            AdvancedAPI.Controls.FlatDevice.LightToggled -= FlatDeviceLightToggledHandler;
            AdvancedAPI.Controls.FlatDevice.Opened -= FlatDeviceOpenedHandler;
            AdvancedAPI.Controls.FlatDevice.Closed -= FlatDeviceClosedHandler;
            AdvancedAPI.Controls.FlatDevice.BrightnessChanged -= FlatDeviceBrightnessChangedHandler;
            AdvancedAPI.Controls.FlatDevice.RemoveConsumer(this);
        }

        public async void UpdateDeviceInfo(FlatDeviceInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("FLATDEVICE");
        }
    }

    public partial class ControllerV2
    {
        [Route(HttpVerbs.Get, "/equipment/flatdevice/info")]
        public void FlatDeviceInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IFlatDeviceMediator flat = AdvancedAPI.Controls.FlatDevice;

                FlatDeviceInfo info = flat.GetInfo();
                response.Response = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/flatdevice/set-light")]
        public void FlatDeviceToggle([QueryField] bool on)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IFlatDeviceMediator flat = AdvancedAPI.Controls.FlatDevice;

                if (flat.GetInfo().Connected)
                {
                    BackgroundOperation.Report(flat.ToggleLight(on, AdvancedAPI.Controls.StatusMediator.GetStatus(), new CancellationTokenSource().Token), "FLATDEVICE-LIGHT-FAILED", "Flat device light");
                    response.Response = "Flatdevice light set";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable("Flatdevice not connected", 409);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/flatdevice/set-cover")]
        public void FlatDeviceCover([QueryField] bool closed)
        {
            HttpResponse response = new HttpResponse();
            try
            {
                IFlatDeviceMediator flat = AdvancedAPI.Controls.FlatDevice;

                if (flat.GetInfo().Connected)
                {
                    if (closed)
                    {
                        BackgroundOperation.Report(flat.CloseCover(AdvancedAPI.Controls.StatusMediator.GetStatus(), new CancellationTokenSource().Token), "FLATDEVICE-COVER-FAILED", "Flat device cover close");
                    }
                    else
                    {
                        BackgroundOperation.Report(flat.OpenCover(AdvancedAPI.Controls.StatusMediator.GetStatus(), new CancellationTokenSource().Token), "FLATDEVICE-COVER-FAILED", "Flat device cover open");
                    }
                    response.Response = "Flatdevice cover set";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable("Flatdevice not connected", 409);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/flatdevice/set-brightness")]
        public void FlatDeviceSetLight([QueryField] int brightness)
        {
            HttpResponse response = new HttpResponse();
            try
            {
                IFlatDeviceMediator flat = AdvancedAPI.Controls.FlatDevice;

                if (flat.GetInfo().Connected)
                {
                    BackgroundOperation.Report(flat.SetBrightness(brightness, AdvancedAPI.Controls.StatusMediator.GetStatus(), new CancellationTokenSource().Token), "FLATDEVICE-BRIGHTNESS-FAILED", "Flat device brightness");
                    response.Response = "Flatdevice brightness set";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable("Flatdevice not connected", 409);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/flatdevice/set-heater")]
        public void FlatDeviceSetHeater([QueryField] int power)
        {
            HttpResponse response = new HttpResponse();
            try
            {
                if (!AdvancedAPI.Controls.FlatDevice.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("FlatDevice not connected", 409));
                }
                else
                {
                    var mediator = AdvancedAPI.Controls.FlatDevice;
                    var mediatorType = mediator.GetType();

                    var handlerField = mediatorType.GetField("handler",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    object handler = null;
                    if (handlerField != null)
                    {
                        handler = handlerField.GetValue(mediator);
                    }

                    object device = null;
                    if (handler != null)
                    {
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);
                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active flat device available", 500));
                    }
                    else
                    {
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        if (deviceTypeName.Contains("Wanderer"))
                        {
                            var heaterProperty = deviceType.GetProperty("HeaterPower",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (heaterProperty != null)
                            {
                                var setMethod = heaterProperty.GetSetMethod(true);
                                if (setMethod != null)
                                {
                                    setMethod.Invoke(device, new object[] { power });
                                    response.Response = "Heater set";
                                }
                                else
                                {
                                    response = CoreUtility.CreateErrorTable(new Error("Heater property has no setter", 501));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererCover does not have Heater property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Heater control is only supported on WandererCover. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/flatdevice/get-heater")]
        public void FlatDeviceGetHeater()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                if (!AdvancedAPI.Controls.FlatDevice.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("FlatDevice not connected", 409));
                }
                else
                {
                    var mediator = AdvancedAPI.Controls.FlatDevice;
                    var mediatorType = mediator.GetType();

                    var handlerField = mediatorType.GetField("handler",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    object handler = null;
                    if (handlerField != null)
                    {
                        handler = handlerField.GetValue(mediator);
                    }

                    object device = null;
                    if (handler != null)
                    {
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);
                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active flat device available", 500));
                    }
                    else
                    {
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        if (deviceTypeName.Contains("Wanderer"))
                        {
                            var heaterProperty = deviceType.GetProperty("HeaterPower",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (heaterProperty != null)
                            {
                                try
                                {
                                    var heaterValue = heaterProperty.GetValue(device);
                                    response.Response = heaterValue;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Failed to get Heater value: {ex.Message}");
                                    response = CoreUtility.CreateErrorTable(new Error($"Failed to get heater: {ex.InnerException?.Message ?? ex.Message}", 500));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererCover does not have Heater property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Heater control is only supported on WandererCover. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/flatdevice/set-openposition")]
        public void FlatDeviceSetOpenPosition([QueryField] float angle)
        {
            HttpResponse response = new HttpResponse();
            try
            {
                if (!AdvancedAPI.Controls.FlatDevice.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("FlatDevice not connected", 409));
                }
                else
                {
                    var mediator = AdvancedAPI.Controls.FlatDevice;
                    var mediatorType = mediator.GetType();

                    var handlerField = mediatorType.GetField("handler",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    object handler = null;
                    if (handlerField != null)
                    {
                        handler = handlerField.GetValue(mediator);
                    }

                    object device = null;
                    if (handler != null)
                    {
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);
                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active flat device available", 500));
                    }
                    else
                    {
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        if (deviceTypeName.Contains("Wanderer"))
                        {
                            var openPositionProperty = deviceType.GetProperty("OpenPositionAngle",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (openPositionProperty != null)
                            {
                                var setMethod = openPositionProperty.GetSetMethod(true);
                                if (setMethod != null)
                                {
                                    setMethod.Invoke(device, new object[] { angle });
                                    response.Response = "Open position set";
                                }
                                else
                                {
                                    response = CoreUtility.CreateErrorTable(new Error("OpenPosition property has no setter", 501));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererCover does not have OpenPosition property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Open position control is only supported on WandererCover. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/flatdevice/get-openposition")]
        public void FlatDeviceGetOpenPosition()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                if (!AdvancedAPI.Controls.FlatDevice.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("FlatDevice not connected", 409));
                }
                else
                {
                    var mediator = AdvancedAPI.Controls.FlatDevice;
                    var mediatorType = mediator.GetType();

                    var handlerField = mediatorType.GetField("handler",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    object handler = null;
                    if (handlerField != null)
                    {
                        handler = handlerField.GetValue(mediator);
                    }

                    object device = null;
                    if (handler != null)
                    {
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);
                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active flat device available", 500));
                    }
                    else
                    {
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        if (deviceTypeName.Contains("Wanderer"))
                        {
                            var openPositionProperty = deviceType.GetProperty("OpenPositionAngle",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (openPositionProperty != null)
                            {
                                try
                                {
                                    var openPositionValue = openPositionProperty.GetValue(device);
                                    response.Response = openPositionValue;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Failed to get OpenPosition value: {ex.Message}");
                                    response = CoreUtility.CreateErrorTable(new Error($"Failed to get open position: {ex.InnerException?.Message ?? ex.Message}", 500));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererCover does not have OpenPosition property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Open position control is only supported on WandererCover. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/flatdevice/set-closedposition")]
        public void FlatDeviceSetClosedPosition([QueryField] float angle)
        {
            HttpResponse response = new HttpResponse();
            try
            {
                if (!AdvancedAPI.Controls.FlatDevice.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("FlatDevice not connected", 409));
                }
                else
                {
                    var mediator = AdvancedAPI.Controls.FlatDevice;
                    var mediatorType = mediator.GetType();

                    var handlerField = mediatorType.GetField("handler",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    object handler = null;
                    if (handlerField != null)
                    {
                        handler = handlerField.GetValue(mediator);
                    }

                    object device = null;
                    if (handler != null)
                    {
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);
                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active flat device available", 500));
                    }
                    else
                    {
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        if (deviceTypeName.Contains("Wanderer"))
                        {
                            var closedPositionProperty = deviceType.GetProperty("ClosePositionAngle",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (closedPositionProperty != null)
                            {
                                var setMethod = closedPositionProperty.GetSetMethod(true);
                                if (setMethod != null)
                                {
                                    setMethod.Invoke(device, new object[] { angle });
                                    response.Response = "Closed position set";
                                }
                                else
                                {
                                    response = CoreUtility.CreateErrorTable(new Error("ClosePosition property has no setter", 501));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererCover does not have ClosedPosition property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Closed position control is only supported on WandererCover. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/flatdevice/get-closedposition")]
        public void FlatDeviceGetClosedPosition()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                if (!AdvancedAPI.Controls.FlatDevice.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("FlatDevice not connected", 409));
                }
                else
                {
                    var mediator = AdvancedAPI.Controls.FlatDevice;
                    var mediatorType = mediator.GetType();

                    var handlerField = mediatorType.GetField("handler",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    object handler = null;
                    if (handlerField != null)
                    {
                        handler = handlerField.GetValue(mediator);
                    }

                    object device = null;
                    if (handler != null)
                    {
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);
                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active flat device available", 500));
                    }
                    else
                    {
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        if (deviceTypeName.Contains("Wanderer"))
                        {
                            var closedPositionProperty = deviceType.GetProperty("ClosePositionAngle",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (closedPositionProperty != null)
                            {
                                try
                                {
                                    var closedPositionValue = closedPositionProperty.GetValue(device);
                                    response.Response = closedPositionValue;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Failed to get ClosedPosition value: {ex.Message}");
                                    response = CoreUtility.CreateErrorTable(new Error($"Failed to get closed position: {ex.InnerException?.Message ?? ex.Message}", 500));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererCover does not have ClosedPosition property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Closed position control is only supported on WandererCover. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/flatdevice/get-currentposition")]
        public void FlatDeviceGetCurrentPosition()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                if (!AdvancedAPI.Controls.FlatDevice.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("FlatDevice not connected", 409));
                }
                else
                {
                    var mediator = AdvancedAPI.Controls.FlatDevice;
                    var mediatorType = mediator.GetType();

                    var handlerField = mediatorType.GetField("handler",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    object handler = null;
                    if (handlerField != null)
                    {
                        handler = handlerField.GetValue(mediator);
                    }

                    object device = null;
                    if (handler != null)
                    {
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);
                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active flat device available", 500));
                    }
                    else
                    {
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        if (deviceTypeName.Contains("Wanderer"))
                        {
                            var currentPositionProperty = deviceType.GetProperty("CurrentPositionAngle",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (currentPositionProperty != null)
                            {
                                try
                                {
                                    var currentPositionValue = currentPositionProperty.GetValue(device);
                                    response.Response = currentPositionValue;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Failed to get Position value: {ex.Message}");
                                    response = CoreUtility.CreateErrorTable(new Error($"Failed to get current position: {ex.InnerException?.Message ?? ex.Message}", 500));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererCover does not have Position property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Current position is only supported on WandererCover. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/flatdevice/get-settings")]
        public void FlatDeviceGetSettings()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.FlatDevice.GetDevice();
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("FlatDevice device not available", 409));
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

        [Route(HttpVerbs.Get, "/equipment/flatdevice/set-setting")]
        public void FlatDeviceSetSetting([QueryField] string settingName, [QueryField] string newValue)
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
                    var device = AdvancedAPI.Controls.FlatDevice.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("FlatDevice device not available", 409));
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
