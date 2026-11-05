#region "copyright"

/*
    Copyright © 2024 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.WPF.Base.Interfaces.ViewModel;
using ninaAPI.Properties;
using ninaAPI.Utility;

namespace ninaAPI.WebService.V2
{
    public partial class ControllerV2
    {
        [Route(HttpVerbs.Get, "/application/switch-tab")]
        public void ApplicationSwitchTab([QueryField] string tab)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                response.Response = "Switched tab";
                switch (tab)
                {
                    case "equipment":
                        AdvancedAPI.Controls.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.EQUIPMENT);
                        break;
                    case "skyatlas":
                        AdvancedAPI.Controls.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.SKYATLAS);
                        break;
                    case "framing":
                        AdvancedAPI.Controls.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.FRAMINGASSISTANT);
                        break;
                    case "flatwizard":
                        AdvancedAPI.Controls.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.FLATWIZARD);
                        break;
                    case "sequencer":
                        AdvancedAPI.Controls.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.SEQUENCE);
                        break;
                    case "imaging":
                        AdvancedAPI.Controls.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.IMAGING);
                        break;
                    case "options":
                        AdvancedAPI.Controls.Application.ChangeTab(NINA.Core.Enum.ApplicationTab.OPTIONS);
                        break;
                    default:
                        response = CoreUtility.CreateErrorTable(new Error("Invalid application tab", 400));
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/application/get-tab")]
        public void ApplicationGetTab()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IApplicationVM vm = (IApplicationVM)AdvancedAPI.Controls.Application.GetType().GetField("handler", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(AdvancedAPI.Controls.Application);
                int index = vm.TabIndex;
                switch (index)
                {
                    case 0:
                        response.Response = "equipment";
                        break;
                    case 1:
                        response.Response = "skyatlas";
                        break;
                    case 2:
                        response.Response = "framing";
                        break;
                    case 3:
                        response.Response = "flatwizard";
                        break;
                    case 4:
                        response.Response = "sequencer";
                        break;
                    case 5:
                        response.Response = "imaging";
                        break;
                    case 6:
                        response.Response = "options";
                        break;
                    default:
                        response = CoreUtility.CreateErrorTable(new Error("Invalid application tab", 400));
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        // PI 'N' Stars runs headless: there is no screen to capture, and the capture threw (after allocating a screen-sized
        // bitmap) and logged a stack trace on every call
        [Route(HttpVerbs.Get, "/application/screenshot")]
        public void ApplicationScreenshot()
        {
            HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error("Screenshots are not available on PI 'N' Stars (headless)", 501)));
        }

        [Route(HttpVerbs.Get, "/application/plugins")]
        public void ApplicationPlugins()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                string path = Directory.GetParent(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)).FullName;
                List<string> plugins = [.. Directory.GetDirectories(path).Select(Path.GetFileName)];
                response.Response = plugins;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/application/logs")]
        public void GetRecentLogs([QueryField(true)] int lineCount, [QueryField] string level)
        {
            HttpResponse response = new HttpResponse();

            List<Hashtable> logs = new List<Hashtable>();

            if (string.IsNullOrEmpty(level))
            {
                level = string.Empty;
            }

            try
            {
                string currentLogFile = Directory.GetFiles(Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "Logs")).OrderByDescending(File.GetCreationTime).First();

                string[] logLines = [];

                using (var stream = new FileStream(currentLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (var reader = new StreamReader(stream))
                    {
                        string content = reader.ReadToEnd();
                        logLines = content.Split('\n');
                    }
                }

                List<string> filteredLogLines = new List<string>();
                foreach (string line in logLines)
                {
                    bool valid = true;

                    if (!line.Contains('|' + level + '|') && !string.IsNullOrEmpty(level))
                    {
                        valid = false;
                    }
                    if (line.Contains("DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE"))
                    {
                        valid = false;
                    }
                    if (valid)
                    {
                        filteredLogLines.Add(line);
                    }
                }
                IEnumerable<string> lines = filteredLogLines.TakeLast(lineCount);
                foreach (string line in lines)
                {
                    string[] parts = line.Split('|');
                    if (parts.Length >= 6)
                    {
                        logs.Add(new Hashtable() {
                            { "Timestamp", parts[0] },
                            { "Level", parts[1] },
                            { "Source", parts[2] },
                            { "Member", parts[3] },
                            { "Line", parts[4] },
                            { "Message", string.Join('|', parts.Skip(5)).Trim() }
                        });
                    }
                }
                logs.Reverse();

                response.Response = logs;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/plugin/settings")]
        public void GetPluginSettings()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                response.Response = new
                {
                    AccessControlHeaderEnabled = Settings.Default.UseAccessControlHeader,
                    ShouldCreateThumbnails = Settings.Default.CreateThumbnails,
                };
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        // [Route(HttpVerbs.Get, "/application/windows")]
        // public void GetApplicationWindows()
        // {
        //     HttpResponse response = new HttpResponse();

        //     try
        //     {
        //         response.Response = CreateWindowMeta();
        //     }
        //     catch (Exception ex)
        //     {
        //         Logger.Error(ex);
        //         response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
        //     }

        //     HttpContext.WriteToResponse(response);
        // }

        // [Route(HttpVerbs.Get, "/application/windows/close")]
        // public void GetApplicationWindows([QueryField] int windowId)
        // {
        //     HttpResponse response = new HttpResponse();

        //     try
        //     {
        //         var windowMeta = CreateWindowMeta().Find(w => w.Id == windowId);
        //         if (windowMeta is not null)
        //         {
        //             System.Windows.Application.Current.Dispatcher.Invoke(() =>
        //             {
        //                 foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
        //                 {
        //                     if (window.DataContext?.GetType().FullName == windowMeta.DataContextClassName && window.Title == windowMeta.Title)
        //                     {
        //                         window.Close();
        //                         break;
        //                     }
        //                 }
        //             });

        //             response.Response = "Closed window";
        //         }
        //         else
        //         {
        //             response = CoreUtility.CreateErrorTable(new Error("Invalid window id", 400));
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         Logger.Error(ex);
        //         response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
        //     }

        //     HttpContext.WriteToResponse(response);
        // }

        // private List<WindowMeta> CreateWindowMeta()
        // {
        //     List<WindowMeta> windows = new List<WindowMeta>();
        //     System.Windows.Application.Current.Dispatcher.Invoke(() =>
        //     {
        //         foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
        //         {
        //             WindowMeta meta = new WindowMeta
        //             {
        //                 Title = window.Title,
        //                 DataContextClassName = window.DataContext?.GetType().FullName,
        //             };
        //             windows.Add(meta);
        //         }
        //     });

        //     return windows;
        // }
    }

    internal class WindowMeta
    {
        public int Id => Math.Abs((Title + DataContextClassName).GetHashCode());
        public string Title { get; set; }
        public string DataContextClassName { get; set; }
    }
}
