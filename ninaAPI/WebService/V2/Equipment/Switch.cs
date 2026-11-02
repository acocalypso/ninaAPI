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
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Interfaces.Mediator;
using ninaAPI.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class SwitchWatcher : INinaWatcher, ISwitchConsumer
    {
        private readonly Func<object, EventArgs, Task> SwitchConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("SWITCH-CONNECTED");
        private readonly Func<object, EventArgs, Task> SwitchDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("SWITCH-DISCONNECTED");

        public void Dispose()
        {
            AdvancedAPI.Controls.Switch.RemoveConsumer(this);
        }

        public void StartWatchers()
        {
            AdvancedAPI.Controls.Switch.Connected += SwitchConnectedHandler;
            AdvancedAPI.Controls.Switch.Disconnected += SwitchDisconnectedHandler;
            AdvancedAPI.Controls.Switch.RegisterConsumer(this);
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.Switch.Connected -= SwitchConnectedHandler;
            AdvancedAPI.Controls.Switch.Disconnected -= SwitchDisconnectedHandler;
            AdvancedAPI.Controls.Switch.RemoveConsumer(this);
        }

        public async void UpdateDeviceInfo(SwitchInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("SWITCH");
        }
    }

    public partial class ControllerV2
    {
        private static CancellationTokenSource SwitchToken;

        [Route(HttpVerbs.Get, "/equipment/switch/info")]
        public void SwitchInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ISwitchMediator sw = AdvancedAPI.Controls.Switch;

                SwitchInfo info = sw.GetInfo();
                response.Response = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/switch/set")]
        public async Task SwitchSet([QueryField] short index, [QueryField] double value)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ISwitchMediator sw = AdvancedAPI.Controls.Switch;
                var info = sw.GetInfo();

                if (!info.Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Switch not connected", 409));
                }
                else if (index < 0 || index >= (info.WritableSwitches?.Count ?? 0))
                {
                    // The switch VM only logs an unknown index, and the answer said "updated"
                    response = CoreUtility.CreateErrorTable(new Error($"No writable switch at index {index}", 404));
                }
                else
                {
                    SwitchToken?.Cancel();
                    SwitchToken = new CancellationTokenSource();
                    try
                    {
                        await sw.SetSwitchValue(index, value, AdvancedAPI.Controls.StatusMediator.GetStatus(), SwitchToken.Token);
                        response.Response = "Switch value updated";
                    }
                    catch (OperationCanceledException)
                    {
                        response.Response = "Switch value change replaced by a newer one";
                    }
                    catch (Exception ex)
                    {
                        // A failed write answered "updated" before, since the write wasn't awaited
                        Logger.Error(ex);
                        response.Success = false;
                        response.Response = "Switch value not updated";
                        response.Error = ex.Message;
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

        [Route(HttpVerbs.Get, "/equipment/switch/get-settings")]
        public void SwitchGetSettings()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.Switch.GetDevice();
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Switch device not available", 409));
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

        [Route(HttpVerbs.Get, "/equipment/switch/set-setting")]
        public void SwitchSetSetting([QueryField] string settingName, [QueryField] string newValue)
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
                    var device = AdvancedAPI.Controls.Switch.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Switch device not available", 409));
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
