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
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Interfaces.Mediator;
using NINA.WPF.Base.Mediator;
using NINA.WPF.Base.ViewModel.Equipment.Rotator;
using ninaAPI.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class RotatorWatcher : INinaWatcher, IRotatorConsumer
    {
        private readonly Func<object, EventArgs, Task> RotatorConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("ROTATOR-CONNECTED");
        private readonly Func<object, EventArgs, Task> RotatorDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("ROTATOR-DISCONNECTED");

        public void Dispose()
        {
            AdvancedAPI.Controls.Rotator.RemoveConsumer(this);
        }

        public void StartWatchers()
        {
            AdvancedAPI.Controls.Rotator.Connected += RotatorConnectedHandler;
            AdvancedAPI.Controls.Rotator.Disconnected += RotatorDisconnectedHandler;
            AdvancedAPI.Controls.Rotator.Moved += RotatorMovedHandler;
            AdvancedAPI.Controls.Rotator.MovedMechanical += RotatorMovedMechanicalHandler;
            AdvancedAPI.Controls.Rotator.Synced += RotatorSyncedHandler;
            AdvancedAPI.Controls.Rotator.RegisterConsumer(this);
        }

        private async void RotatorSyncedHandler(object sender, RotatorEventArgs e)
        {
            await WebSocketV2.SendAndAddEvent("ROTATOR-SYNCED");
        }

        private async Task RotatorMovedMechanicalHandler(object arg1, RotatorEventArgs args)
        {
            await WebSocketV2.SendAndAddEvent("ROTATOR-MOVED-MECHANICAL", DateTime.Now, new Dictionary<string, object>() {
                { "From", args.From },
                { "To", args.To }
            });
        }

        private async Task RotatorMovedHandler(object arg1, RotatorEventArgs args)
        {
            await WebSocketV2.SendAndAddEvent("ROTATOR-MOVED", DateTime.Now, new Dictionary<string, object>() {
                { "From", args.From },
                { "To", args.To }
            });
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.Rotator.Connected -= RotatorConnectedHandler;
            AdvancedAPI.Controls.Rotator.Disconnected -= RotatorDisconnectedHandler;
            AdvancedAPI.Controls.Rotator.Moved -= RotatorMovedHandler;
            AdvancedAPI.Controls.Rotator.MovedMechanical -= RotatorMovedMechanicalHandler;
            AdvancedAPI.Controls.Rotator.Synced -= RotatorSyncedHandler;
            AdvancedAPI.Controls.Rotator.RemoveConsumer(this);
        }

        public async void UpdateDeviceInfo(RotatorInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("ROTATOR");
        }
    }

    public partial class ControllerV2
    {
        private static CancellationTokenSource RotatorToken;


        [Route(HttpVerbs.Get, "/equipment/rotator/info")]
        public void RotatorInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IRotatorMediator rotator = AdvancedAPI.Controls.Rotator;

                RotatorInfo info = rotator.GetInfo();
                response.Response = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/rotator/move")]
        public void RotatorMove([QueryField] float position)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IRotatorMediator rotator = AdvancedAPI.Controls.Rotator;

                if (!rotator.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Rotator not connected", 409));
                }
                else
                {
                    var token = new CancellationTokenSource();
                    Interlocked.Exchange(ref RotatorToken, token)?.Cancel();
                    BackgroundOperation.Report(rotator.Move(position, token.Token), "ROTATOR-MOVE-FAILED", "Rotator move");
                    response.Response = "Rotator move started";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/rotator/move-mechanical")]
        public void RotatorMoveMechanical([QueryField] float position)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IRotatorMediator rotator = AdvancedAPI.Controls.Rotator;

                if (!rotator.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Rotator not connected", 409));
                }
                else
                {
                    var token = new CancellationTokenSource();
                    Interlocked.Exchange(ref RotatorToken, token)?.Cancel();
                    BackgroundOperation.Report(rotator.MoveMechanical(position, token.Token), "ROTATOR-MOVE-FAILED", "Rotator mechanical move");
                    response.Response = "Rotator move started";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/rotator/reverse")]
        public void RotatorReverse([QueryField] bool reverseDirection)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!AdvancedAPI.Controls.Rotator.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Rotator not connected", 409));
                }
                else
                {
                    var rotator = (RotatorVM)typeof(RotatorMediator).GetField("handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(AdvancedAPI.Controls.Rotator);
                    rotator.ReverseCommand.Execute(reverseDirection);

                    response.Response = "Reverse set";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/rotator/set-mechanical-range")]
        public void RotatorSetRange([QueryField(true)] RotatorRangeTypeEnum range, [QueryField] float rangeStartPosition)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                AdvancedAPI.Controls.Profile.ActiveProfile.RotatorSettings.RangeType = range;
                if (!HttpContext.IsParameterOmitted(nameof(rangeStartPosition)))
                {
                    AdvancedAPI.Controls.Profile.ActiveProfile.RotatorSettings.RangeStartMechanicalPosition = rangeStartPosition;
                }

                response.Response = "Range set";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/rotator/stop-move")]
        public void RotatorStopMove()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!AdvancedAPI.Controls.Rotator.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Rotator not connected", 409));
                }
                else
                {
                    Interlocked.Exchange(ref RotatorToken, null)?.Cancel();

                    // NINA's stop button: it cancels the rotator VM's current move, whoever started it (a sequence's
                    // rotate then goes on as if it was done), and halts the device
                    var rotatorVM = (RotatorVM)typeof(RotatorMediator).GetField("handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(AdvancedAPI.Controls.Rotator);
                    rotatorVM.HaltCommand.Execute(null);
                    response.Response = "Rotator move stopped";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/rotator/reset-position")]
        public void RotatorResetPosition()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!AdvancedAPI.Controls.Rotator.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Rotator not connected", 409));
                }
                else
                {
                    // get RotatorVM handler and call ResetPosition on underlying device if available
                    var rotatorVm = (RotatorVM)typeof(RotatorMediator).GetField("handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(AdvancedAPI.Controls.Rotator);
                    var device = rotatorVm?.Rotator;
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No rotator device available", 500));
                    }
                    else
                    {
                        var mi = device.GetType().GetMethod("ResetPosition", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (mi != null)
                        {
                            mi.Invoke(device, null);
                            response.Response = "ResetPosition invoked";
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Reset not supported by this rotator", 501));
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

        [Route(HttpVerbs.Get, "/equipment/rotator/set-backlash")]
        public void RotatorSetBacklash([QueryField] float angle)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!AdvancedAPI.Controls.Rotator.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Rotator not connected", 409));
                }
                else
                {
                    // Get the device from the mediator via the handler
                    var mediator = AdvancedAPI.Controls.Rotator;

                    // The mediator has a handler (IRotatorVM) which can give us the actual device
                    var mediatorType = mediator.GetType();

                    // Try to get handler field (it's protected)
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
                        // Call GetDevice() method on the handler to get the actual device
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);

                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active rotator device available", 500));
                    }
                    else
                    {
                        // Check if device is WandererRotator or has Backlash property
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        // Check if it's WandererRotator by type name or try to get Backlash property
                        if (deviceTypeName.Contains("WandererRotator") || deviceTypeName.Contains("Wanderer"))
                        {
                            // Try to get and invoke the Backlash property/method
                            var backlashProperty = deviceType.GetProperty("Backlash",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (backlashProperty != null)
                            {
                                // If it's a property with a setter, we need to invoke it appropriately
                                var setMethod = backlashProperty.GetSetMethod(true);
                                if (setMethod != null)
                                {
                                    // Invoke setter with float backlash angle
                                    try
                                    {
                                        setMethod.Invoke(device, new object[] { angle });
                                        response.Response = $"Backlash setter invoked successfully with angle {angle}°";
                                    }
                                    catch (Exception ex)
                                    {
                                        response = CoreUtility.CreateErrorTable(new Error($"Failed to set backlash: {ex.InnerException?.Message ?? ex.Message}", 500));
                                    }
                                }
                                else
                                {
                                    response = CoreUtility.CreateErrorTable(new Error("Backlash property has no setter", 501));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererRotator does not have Backlash property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Backlash is only supported on WandererRotator. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/rotator/get-backlash")]
        public void RotatorGetBacklash()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!AdvancedAPI.Controls.Rotator.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Rotator not connected", 409));
                }
                else
                {
                    // Get the device from the mediator via the handler
                    var mediator = AdvancedAPI.Controls.Rotator;
                    var mediatorType = mediator.GetType();

                    // Try to get handler field (it's protected)
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
                        // Call GetDevice() method on the handler to get the actual device
                        var getDeviceMethod = handler.GetType().GetMethod("GetDevice",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                        if (getDeviceMethod != null)
                        {
                            device = getDeviceMethod.Invoke(handler, null);
                        }
                    }

                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No active rotator device available", 500));
                    }
                    else
                    {
                        // Check if device is WandererRotator
                        var deviceType = device.GetType();
                        var deviceTypeName = deviceType.Name;

                        if (deviceTypeName.Contains("WandererRotator") || deviceTypeName.Contains("Wanderer"))
                        {
                            // Try to get the Backlash property value
                            var backlashProperty = deviceType.GetProperty("Backlash",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                            if (backlashProperty != null)
                            {
                                try
                                {
                                    var backlashValue = backlashProperty.GetValue(device);
                                    response.Response = backlashValue;
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Failed to get Backlash value: {ex.Message}");
                                    response = CoreUtility.CreateErrorTable(new Error($"Failed to get backlash: {ex.InnerException?.Message ?? ex.Message}", 500));
                                }
                            }
                            else
                            {
                                response = CoreUtility.CreateErrorTable(new Error("WandererRotator does not have Backlash property", 501));
                            }
                        }
                        else
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Backlash is only supported on WandererRotator. Current device: {deviceTypeName}", 501));
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

        [Route(HttpVerbs.Get, "/equipment/rotator/get-settings")]
        public void RotatorGetSettings()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.Rotator.GetDevice();
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Rotator device not available", 409));
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

        [Route(HttpVerbs.Get, "/equipment/rotator/set-setting")]
        public void RotatorSetSetting([QueryField] string settingName, [QueryField] string newValue)
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
                    var device = AdvancedAPI.Controls.Rotator.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Rotator device not available", 409));
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
