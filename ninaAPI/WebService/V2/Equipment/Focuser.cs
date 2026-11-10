#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Accord;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using NINA.Core.Locale;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.WPF.Base.Mediator;
using NINA.WPF.Base.Utility.AutoFocus;
using NINA.WPF.Base.ViewModel.Equipment.Focuser;
using ninaAPI.Utility;
using OxyPlot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class FocuserWatcher : INinaWatcher, IFocuserConsumer
    {
        private readonly Func<object, EventArgs, Task> FocuserConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("FOCUSER-CONNECTED");
        private readonly Func<object, EventArgs, Task> FocuserDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("FOCUSER-DISCONNECTED");

        public void Dispose()
        {
            AdvancedAPI.Controls.Focuser.RemoveConsumer(this);
        }

        public void StartWatchers()
        {
            AdvancedAPI.Controls.Focuser.Connected += FocuserConnectedHandler;
            AdvancedAPI.Controls.Focuser.Disconnected += FocuserDisconnectedHandler;
            AdvancedAPI.Controls.Focuser.RegisterConsumer(this);
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.Focuser.Connected -= FocuserConnectedHandler;
            AdvancedAPI.Controls.Focuser.Disconnected -= FocuserDisconnectedHandler;
            AdvancedAPI.Controls.Focuser.RemoveConsumer(this);
        }

        public async void UpdateDeviceInfo(FocuserInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("FOCUSER");
        }

        public async void UpdateEndAutoFocusRun(AutoFocusInfo info)
        {
            await WebSocketV2.SendAndAddEvent("AUTOFOCUS-FINISHED");
        }

        public async void AutoFocusRunStarting()
        {
            await WebSocketV2.SendAndAddEvent("AUTOFOCUS-STARTING");
        }

        public async void NewAutoFocusPoint(DataPoint dataPoint)
        {
            await WebSocketV2.SendAndAddEvent("AUTOFOCUS-POINT-ADDED", DateTime.Now, new Dictionary<string, object>()
            {
                ["HFR"] = dataPoint.Y,
                ["Position"] = dataPoint.X,
            });
        }

        public async void UpdateUserFocused(FocuserInfo info)
        {
            await WebSocketV2.SendAndAddEvent("FOCUSER-USER-FOCUSED");
        }
    }

    public partial class ControllerV2
    {
        private static CancellationTokenSource AutoFocusToken;

        // the API's autofocus run until it has ended, its cleanup included: a second start cancelled the run and began a
        // new one at once, while the old one was still moving the focuser back
        private static Task AutoFocusTask;
        private static readonly object AutoFocusStartLock = new();

        [Route(HttpVerbs.Get, "/equipment/focuser/info")]
        public void FocuserInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IFocuserMediator focuser = AdvancedAPI.Controls.Focuser;
                response.Response = focuser.GetInfo();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        // The API's own move. Its token and task change together under the lock, so parallel move requests can't lose a
        // token and stop-move can tell whether the move it cancels was still running
        private static readonly object FocuserMoveLock = new();
        private static CancellationTokenSource FocuserToken;
        private static Task FocuserMoveTask = Task.CompletedTask;

        [Route(HttpVerbs.Get, "/equipment/focuser/move")]
        public void FocuserMove([QueryField] int position)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IFocuserMediator focuser = AdvancedAPI.Controls.Focuser;
                if (!focuser.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Focuser not connected", 409));
                }
                else
                {
                    Task move;
                    lock (FocuserMoveLock)
                    {
                        FocuserToken?.Cancel();
                        FocuserToken = new CancellationTokenSource();
                        move = focuser.MoveFocuser(position, FocuserToken.Token);
                        FocuserMoveTask = move;
                    }

                    BackgroundOperation.Report(move, "FOCUSER-MOVE-FAILED", "Focuser move");
                    response.Response = "Move started";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }
#if !WINDOWS
        [Route(HttpVerbs.Get, "/equipment/focuser/maxstep")]
        public void FocuserMaxStep([QueryField] int position)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!CoreUtil.IsLinux())
                {
                    var platform = CoreUtil.UserAgent;
                    response = CoreUtility.CreateErrorTable(new Error($"MaxStep endpoint only available on Linux ({platform})", 403));
                    HttpContext.WriteToResponse(response);
                    return;
                }

                IFocuserMediator focuser = AdvancedAPI.Controls.Focuser;
                if (!focuser.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Focuser not connected", 409));
                }
                else
                {
                    if (!focuser.GetInfo().CanSetMaxStep)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Focuser cannot set max step", 409));
                    }
                    else
                    {
                        focuser.SetMaxStep(position);
                        response.Response = "Focuser max step set";
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

        [Route(HttpVerbs.Get, "/equipment/focuser/reverse")]
        public void FocuserReverse([QueryField] bool value)
        {
            HttpResponse response = new();

            try
            {
                if (!CoreUtil.IsLinux())
                {
                    var platform = CoreUtil.UserAgent;
                    response = CoreUtility.CreateErrorTable(new Error($"Reverse endpoint only available on Linux ({platform})", 403));
                    HttpContext.WriteToResponse(response);
                    return;
                }

                IFocuserMediator focuser = AdvancedAPI.Controls.Focuser;

                if (!focuser.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Focuser not connected", 409));
                }
                else
                {
                    if (!focuser.GetInfo().CanReverse)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Focuser cannot set reverse", 409));
                    }
                    else
                    {
                        focuser.SetReverse(value);
                        response.Response = "Focuser reverse set";
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
#endif

        [Route(HttpVerbs.Get, "/equipment/focuser/stop-move")]
        public void FocuserStopMove()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IFocuserMediator focuser = AdvancedAPI.Controls.Focuser;
                if (!focuser.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Focuser not connected", 409));
                }
                else
                {
                    // Only the API's own move can be stopped here: autofocus, the sequence and filter offsets move
                    // with their own token (NINA has no halt for them; a device halt is undone by their move loops).
                    // An API move still waiting for one of them to release the focuser counts as running: it is what
                    // gets stopped, and the other operation goes on.
                    bool apiMoveRunning;
                    lock (FocuserMoveLock)
                    {
                        apiMoveRunning = !FocuserMoveTask.IsCompleted;
                        FocuserToken?.Cancel();
                        FocuserToken = null;
                    }

                    if (apiMoveRunning)
                    {
                        response.Response = "Focuser move stopped";
                    }
                    else if (focuser.GetInfo().IsMoving)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("The focuser is moved by another operation (autofocus, sequence); stop that operation", 409));
                    }
                    else
                    {
                        response.Response = "No focuser move was running";
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

        // Only works if it was started by the api
        [Route(HttpVerbs.Get, "/equipment/focuser/auto-focus")]
        public void FocuserStartAF([QueryField] bool cancel)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IFocuserMediator focuser = AdvancedAPI.Controls.Focuser;
                if (cancel)
                {
                    AutoFocusToken?.Cancel();
                    response.Response = "Autofocus canceled";
                }
                else if (!focuser.GetInfo().Connected)
                {
                    // the start went on after this answer was set, and the run failed on the missing focuser
                    response = CoreUtility.CreateErrorTable(new Error("Focuser not connected", 409));
                }
                else
                {
                    lock (AutoFocusStartLock)
                    {
                        if (AutoFocusTask is { IsCompleted: false })
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Autofocus already running", 409));
                        }
                        else
                        {
                            AutoFocusToken = new CancellationTokenSource();

                            var service = AdvancedAPI.Controls.WindowFactory.Create();

                            var autofocus = AdvancedAPI.Controls.AutoFocusFactory.Create();
                            service.Show(autofocus, Loc.Instance["LblAutoFocus"], System.Windows.ResizeMode.CanResize, System.Windows.WindowStyle.ToolWindow);

                            AutoFocusTask = autofocus.StartAutoFocus(
                                AdvancedAPI.Controls.FilterWheel.GetInfo().SelectedFilter,
                                AutoFocusToken.Token,
                                AdvancedAPI.Controls.StatusMediator.GetStatus()
                            ).ContinueWith(result =>
                            {
                                // result.Result threw for a failed or cancelled run: the window stayed open and the
                                // exception went unobserved
                                if (result.IsCompletedSuccessfully)
                                {
                                    AdvancedAPI.Controls.ImageHistory.AppendAutoFocusPoint(result.Result);
                                }
                                else if (result.IsFaulted)
                                {
                                    Logger.Error(result.Exception.GetBaseException());
                                }

                                service.DelayedClose(TimeSpan.FromSeconds(10));
                            }, TaskScheduler.Default);
                            response.Response = "Autofocus started";
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

        [Route(HttpVerbs.Get, "/equipment/focuser/last-af")]
        public async Task FocuserLastAF()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                string af_folder = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AutoFocus");
                if (Directory.Exists(af_folder))
                {
                    string[] files = Directory.GetFiles(af_folder);
                    if (files.Length > 0)
                    {
                        string newest = files.OrderBy(File.GetCreationTime).Last();
                        string json = await Retry.Do(() => File.ReadAllText(newest), TimeSpan.FromMilliseconds(100), 5);
                        response.Response = JsonConvert.DeserializeObject<AutoFocusReport>(json);
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No AF available", 500));
                    }
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("No AF available", 500));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/focuser/pins/reverse")]
        public async Task FocuserLastAF([QueryField] bool reversing)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (AdvancedAPI.Controls.Focuser.GetType().HasMethod("SetReverse"))
                {
                    AdvancedAPI.Controls.Focuser.GetType().GetMethod("SetReverse").Invoke(AdvancedAPI.Controls.Focuser, [reversing]);
                    response.Response = "Reverse set";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Platform not supported", 500));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/focuser/get-settings")]
        public void FocuserGetSettings()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.Focuser.GetDevice();
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Focuser device not available", 409));
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

        [Route(HttpVerbs.Get, "/equipment/focuser/send-command")]
        public void FocuserSendCommand([QueryField] string command)
        {
            HttpResponse response = new HttpResponse();

            var allowedCommands = new HashSet<string> { "ClearStall", "ResetPosition" };

            try
            {
                if (string.IsNullOrEmpty(command) || !allowedCommands.Contains(command))
                {
                    response = CoreUtility.CreateErrorTable(new Error($"Command '{command}' not allowed", 400));
                }
                else
                {
                    var device = AdvancedAPI.Controls.Focuser.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Focuser device not available", 409));
                    }
                    else
                    {
                        var method = device.GetType().GetMethod(command);
                        if (method == null)
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Command '{command}' not supported by this device", 409));
                        }
                        else
                        {
                            method.Invoke(device, null);
                            response.Response = $"Command '{command}' executed";
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

        [Route(HttpVerbs.Get, "/equipment/focuser/set-setting")]
        public void FocuserSetSetting([QueryField] string settingName, [QueryField] string newValue)
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
                    var device = AdvancedAPI.Controls.Focuser.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Focuser device not available", 409));
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
