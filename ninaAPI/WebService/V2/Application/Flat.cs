#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.SignalR;
using NINA.Core.Utility;
using System.Windows;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.FlatDevice;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using ninaAPI.Utility;

namespace ninaAPI.WebService.V2
{
    public partial class ControllerV2
    {
        private static Task currentFlatTask;

        // the run's failure was never looked at: a run that ended on an exception reported "Finished" and left no log line
        private static Task flatTask
        {
            get => currentFlatTask;
            set
            {
                currentFlatTask = value;
                value?.ContinueWith(t =>
                {
                    var error = t.Exception?.GetBaseException();
                    if (error is not OperationCanceledException)
                    {
                        Logger.Error($"Flat run failed: {error}");
                    }
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }

        // a flat run during a sequence mixed its filter changes and exposures into the sequence's
        private static HttpResponse RefuseWhileSequenceRuns()
        {
            return AdvancedAPI.Controls.Sequence.IsAdvancedSequenceRunning()
                ? CoreUtility.CreateErrorTable(new Error("A sequence is running", 409))
                : null;
        }
        private static CancellationTokenSource flatCancellationToken;
        private static SequentialContainer container;

        /// <summary>
        /// Builds a dark-flat container whose exposure settings mirror those discovered
        /// (or explicitly set) on the completed <paramref name="flatContainer"/>.
        /// </summary>
        private static TrainedDarkFlatExposure BuildDarkFlatsContainer(
            SequentialContainer flatContainer,
            int darkCount,
            bool keepClosed)
        {
            var darks = new TrainedDarkFlatExposure(
                AdvancedAPI.Controls.Profile,
                AdvancedAPI.Controls.Camera,
                AdvancedAPI.Controls.Imaging,
                AdvancedAPI.Controls.ImageSaveMediator,
                AdvancedAPI.Controls.ImageHistory,
                AdvancedAPI.Controls.FilterWheel,
                AdvancedAPI.Controls.FlatDevice);

            darks.GetIterations().Iterations = darkCount;
            darks.KeepPanelClosed = keepClosed;

            TakeExposure exposureItem = null;
            SwitchFilter switchFilterItem = null;

            if (flatContainer is AutoExposureFlat aef)
            {
                exposureItem = aef.GetExposureItem();
                switchFilterItem = aef.GetSwitchFilterItem();
            }
            else if (flatContainer is AutoBrightnessFlat abf)
            {
                exposureItem = abf.GetExposureItem();
                switchFilterItem = abf.GetSwitchFilterItem();
            }
            else if (flatContainer is SkyFlat sf)
            {
                exposureItem = sf.GetExposureItem();
                switchFilterItem = sf.GetSwitchFilterItem();
            }

            if (exposureItem != null)
            {
                darks.GetExposureItem().ExposureTime = exposureItem.ExposureTime;
                darks.GetExposureItem().Gain = exposureItem.Gain;
                darks.GetExposureItem().Offset = exposureItem.Offset;
                darks.GetExposureItem().Binning = exposureItem.Binning;
            }

            if (switchFilterItem != null)
            {
                darks.GetSwitchFilterItem().Filter = switchFilterItem.Filter;
            }

            return darks;
        }

        [Route(HttpVerbs.Get, "/flats/skyflat")]
        public void SkyFlats([QueryField] int count,
                            [QueryField] double minExposure,
                            [QueryField] double maxExposure,
                            [QueryField] double histogramMean,
                            [QueryField] double meanTolerance,
                            [QueryField] bool dither,
                            [QueryField] int filterId,
                            [QueryField] string binning,
                            [QueryField] int gain,
                            [QueryField] int offset,
                            [QueryField] int darkCount)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!flatTask?.IsCompleted ?? false)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Process already running", 400));
                }
                else if (RefuseWhileSequenceRuns() is { } refused)
                {
                    response = refused;
                }
                else if (darkCount > 0)
                {
                    // the dark flats take their exposure time from a trained flat setting, which sky flats never create:
                    // they were dropped without a word. A sky flat's exposure time also changes from frame to frame
                    response = CoreUtility.CreateErrorTable(new Error("Dark flats are not available with sky flats (the exposure time changes from frame to frame)", 400));
                }
                else
                {
                    SkyFlat flats = new SkyFlat(AdvancedAPI.Controls.Profile,
                                                AdvancedAPI.Controls.Camera,
                                                AdvancedAPI.Controls.Mount,
                                                AdvancedAPI.Controls.Imaging,
                                                AdvancedAPI.Controls.ImageSaveMediator,
                                                AdvancedAPI.Controls.ImageHistory,
                                                AdvancedAPI.Controls.FilterWheel,
                                                AdvancedAPI.Controls.TwilightCalculator,
                                                AdvancedAPI.Controls.SymbolBroker);

                    flats.GetIterations().Iterations = count;
                    flats.MaxExposure = HttpContext.IsParameterOmitted(nameof(maxExposure)) ? flats.MaxExposure : maxExposure;
                    flats.MinExposure = HttpContext.IsParameterOmitted(nameof(minExposure)) ? flats.MinExposure : minExposure;
                    flats.HistogramTargetPercentage = HttpContext.IsParameterOmitted(nameof(histogramMean)) ? flats.HistogramTargetPercentage : histogramMean;
                    flats.HistogramTolerancePercentage = HttpContext.IsParameterOmitted(nameof(meanTolerance)) ? flats.HistogramTolerancePercentage : meanTolerance;
                    flats.ShouldDither = HttpContext.IsParameterOmitted(nameof(dither)) ? flats.ShouldDither : dither;
                    flats.GetExposureItem().Gain = HttpContext.IsParameterOmitted(nameof(gain)) ? flats.GetExposureItem().Gain : gain;
                    flats.GetExposureItem().Offset = HttpContext.IsParameterOmitted(nameof(offset)) ? flats.GetExposureItem().Offset : offset;

                    IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;
                    if (!HttpContext.IsParameterOmitted(nameof(filterId)))
                    {
                        if (filterId < 0 || filterId >= profile.FilterWheelSettings.FilterWheelFilters.Count)
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Filter not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetSwitchFilterItem().Filter = profile.FilterWheelSettings.FilterWheelFilters[filterId];
                        }
                    }

                    if (!HttpContext.IsParameterOmitted(nameof(binning)))
                    {
                        if (!AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).Any())
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Binning not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetExposureItem().Binning = AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).First();
                        }
                    }

                    if ((gain < 0 || gain > AdvancedAPI.Controls.Camera.GetInfo().GainMax) && !HttpContext.IsParameterOmitted(nameof(gain)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid gain", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if ((offset < 0 || offset > AdvancedAPI.Controls.Camera.GetInfo().OffsetMax) && !HttpContext.IsParameterOmitted(nameof(offset)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid offset", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if (flats.Validate())
                    {
                        container = flats;
                        flatCancellationToken = new CancellationTokenSource();
                        var progress = AdvancedAPI.Controls.StatusMediator.GetStatus();
                        var token = flatCancellationToken.Token;
                        if (darkCount > 0)
                        {
                            flatTask = Task.Run(async () =>
                            {
                                await flats.Execute(progress, token);
                                if (!token.IsCancellationRequested)
                                {
                                    // waits for the user: after 5 min it answered OK on its own, and the dark flats were
                                    // taken with the scope open; cancelling the flat run ends it
                                    var result = await MyMessageBoxBroadcaster.Instance.ShowMessageBoxAsync(
                                        Loc.Instance["LblCoverScopeMsgBox"],
                                        Loc.Instance["LblCoverScopeMsgBoxTitle"],
                                        MessageBoxButton.OKCancel,
                                        MessageBoxResult.OK,
                                        Timeout.InfiniteTimeSpan,
                                        token);
                                    if (result != MessageBoxResult.OK || token.IsCancellationRequested)
                                        return;

                                    var darks = BuildDarkFlatsContainer(flats, darkCount, keepClosed: false);
                                    container = darks;
                                    if (darks.Validate())
                                        await darks.Execute(progress, token);
                                }
                            });
                        }
                        else
                        {
                            flatTask = flats.Execute(progress, token);
                        }
                        response.Response = "Process started";
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Issues found", 400));
                        response.Response = flats.Issues;
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

        [Route(HttpVerbs.Get, "/flats/auto-brightness")]
        public void AutoBrightnessFlats([QueryField] int count,
                                        [QueryField] int minBrightness,
                                        [QueryField] int maxBrightness,
                                        [QueryField] double histogramMean,
                                        [QueryField] double meanTolerance,
                                        [QueryField] int filterId,
                                        [QueryField] string binning,
                                        [QueryField] int gain,
                                        [QueryField] int offset,
                                        [QueryField] double exposureTime,
                                        [QueryField] bool keepClosed,
                                        [QueryField] int darkCount)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!flatTask?.IsCompleted ?? false)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Process already running", 400));
                }
                else if (RefuseWhileSequenceRuns() is { } refused)
                {
                    response = refused;
                }
                else
                {
                    AutoBrightnessFlat flats = new AutoBrightnessFlat(AdvancedAPI.Controls.Profile,
                                                        AdvancedAPI.Controls.Camera,
                                                        AdvancedAPI.Controls.Imaging,
                                                        AdvancedAPI.Controls.ImageSaveMediator,
                                                        AdvancedAPI.Controls.ImageHistory,
                                                        AdvancedAPI.Controls.FilterWheel,
                                                        AdvancedAPI.Controls.FlatDevice);

                    flats.GetIterations().Iterations = count;
                    flats.MaxBrightness = HttpContext.IsParameterOmitted(nameof(maxBrightness)) ? flats.MaxBrightness : maxBrightness;
                    flats.MinBrightness = HttpContext.IsParameterOmitted(nameof(minBrightness)) ? flats.MinBrightness : minBrightness;

                    // Override the hardcoded [1,100] expression range with the device's actual brightness range
                    // so Validate() accepts values beyond 100 for panels that support them (e.g. 0-255)
                    var flatDeviceInfo = AdvancedAPI.Controls.FlatDevice.GetInfo();
                    double deviceMax = flatDeviceInfo.Connected ? flatDeviceInfo.MaxBrightness : 0; // 0 = ExpressionRange.NO_MAXIMUM
                    flats.MaxBrightnessExpression.Range = new double[] { 0, deviceMax, 0 };
                    flats.MinBrightnessExpression.Range = new double[] { 0, deviceMax, 0 };

                    flats.HistogramTargetPercentage = HttpContext.IsParameterOmitted(nameof(histogramMean)) ? flats.HistogramTargetPercentage : histogramMean;
                    flats.HistogramTolerancePercentage = HttpContext.IsParameterOmitted(nameof(meanTolerance)) ? flats.HistogramTolerancePercentage : meanTolerance;
                    flats.GetExposureItem().Gain = HttpContext.IsParameterOmitted(nameof(gain)) ? flats.GetExposureItem().Gain : gain;
                    flats.GetExposureItem().Offset = HttpContext.IsParameterOmitted(nameof(offset)) ? flats.GetExposureItem().Offset : offset;
                    flats.GetExposureItem().ExposureTime = HttpContext.IsParameterOmitted(nameof(exposureTime)) ? flats.GetExposureItem().ExposureTime : exposureTime;
                    flats.KeepPanelClosed = HttpContext.IsParameterOmitted(nameof(keepClosed)) ? flats.KeepPanelClosed : keepClosed;

                    IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;
                    if (!HttpContext.IsParameterOmitted(nameof(filterId)))
                    {
                        if (filterId < 0 || filterId >= profile.FilterWheelSettings.FilterWheelFilters.Count)
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Filter not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetSwitchFilterItem().Filter = profile.FilterWheelSettings.FilterWheelFilters[filterId];
                        }
                    }

                    if (!HttpContext.IsParameterOmitted(nameof(binning)))
                    {
                        if (!AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).Any())
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Binning not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetExposureItem().Binning = AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).First();
                        }
                    }

                    if ((gain < 0 || gain > AdvancedAPI.Controls.Camera.GetInfo().GainMax) && !HttpContext.IsParameterOmitted(nameof(gain)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid gain", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if ((offset < 0 || offset > AdvancedAPI.Controls.Camera.GetInfo().OffsetMax) && !HttpContext.IsParameterOmitted(nameof(offset)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid offset", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if (flats.Validate())
                    {
                        container = flats;
                        flatCancellationToken = new CancellationTokenSource();
                        var progress = AdvancedAPI.Controls.StatusMediator.GetStatus();
                        var token = flatCancellationToken.Token;
                        if (darkCount > 0)
                        {
                            flatTask = Task.Run(async () =>
                            {
                                await flats.Execute(progress, token);
                                if (!token.IsCancellationRequested)
                                {
                                    // waits for the user: after 5 min it answered OK on its own, and the dark flats were
                                    // taken with the scope open; cancelling the flat run ends it
                                    var result = await MyMessageBoxBroadcaster.Instance.ShowMessageBoxAsync(
                                        Loc.Instance["LblCoverScopeMsgBox"],
                                        Loc.Instance["LblCoverScopeMsgBoxTitle"],
                                        MessageBoxButton.OKCancel,
                                        MessageBoxResult.OK,
                                        Timeout.InfiniteTimeSpan,
                                        token);
                                    if (result != MessageBoxResult.OK || token.IsCancellationRequested)
                                        return;

                                    var darks = BuildDarkFlatsContainer(flats, darkCount, keepClosed);
                                    container = darks;
                                    if (darks.Validate())
                                        await darks.Execute(progress, token);
                                }
                            });
                        }
                        else
                        {
                            flatTask = flats.Execute(progress, token);
                        }
                        response.Response = "Process started";
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Issues found", 400));
                        response.Response = flats.Issues;
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

        [Route(HttpVerbs.Get, "/flats/auto-exposure")]
        public void AutoExposureFlats([QueryField] int count,
                                    [QueryField] double minExposure,
                                    [QueryField] double maxExposure,
                                    [QueryField] double histogramMean,
                                    [QueryField] double meanTolerance,
                                    [QueryField] int brightness,
                                    [QueryField] int filterId,
                                    [QueryField] string binning,
                                    [QueryField] int gain,
                                    [QueryField] int offset,
                                    [QueryField] double exposureTime,
                                    [QueryField] bool keepClosed,
                                    [QueryField] int darkCount)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!flatTask?.IsCompleted ?? false)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Process already running", 400));
                }
                else if (RefuseWhileSequenceRuns() is { } refused)
                {
                    response = refused;
                }
                else
                {
                    AutoExposureFlat flats = new AutoExposureFlat(AdvancedAPI.Controls.Profile,
                                                                AdvancedAPI.Controls.Camera,
                                                                AdvancedAPI.Controls.Imaging,
                                                                AdvancedAPI.Controls.ImageSaveMediator,
                                                                AdvancedAPI.Controls.ImageHistory,
                                                                AdvancedAPI.Controls.FilterWheel,
                                                                AdvancedAPI.Controls.FlatDevice);

                    flats.GetIterations().Iterations = count;
                    flats.MaxExposure = HttpContext.IsParameterOmitted(nameof(maxExposure)) ? flats.MaxExposure : maxExposure;
                    flats.MinExposure = HttpContext.IsParameterOmitted(nameof(minExposure)) ? flats.MinExposure : minExposure;
                    flats.GetSetBrightnessItem().Brightness = HttpContext.IsParameterOmitted(nameof(brightness)) ? flats.GetSetBrightnessItem().Brightness : brightness;
                    flats.HistogramTargetPercentage = HttpContext.IsParameterOmitted(nameof(histogramMean)) ? flats.HistogramTargetPercentage : histogramMean;
                    flats.HistogramTolerancePercentage = HttpContext.IsParameterOmitted(nameof(meanTolerance)) ? flats.HistogramTolerancePercentage : meanTolerance;
                    flats.GetExposureItem().Gain = HttpContext.IsParameterOmitted(nameof(gain)) ? flats.GetExposureItem().Gain : gain;
                    flats.GetExposureItem().Offset = HttpContext.IsParameterOmitted(nameof(offset)) ? flats.GetExposureItem().Offset : offset;
                    flats.GetExposureItem().ExposureTime = HttpContext.IsParameterOmitted(nameof(exposureTime)) ? flats.GetExposureItem().ExposureTime : exposureTime;
                    flats.KeepPanelClosed = HttpContext.IsParameterOmitted(nameof(keepClosed)) ? flats.KeepPanelClosed : keepClosed;

                    IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;
                    if (!HttpContext.IsParameterOmitted(nameof(filterId)))
                    {
                        if (filterId < 0 || filterId >= profile.FilterWheelSettings.FilterWheelFilters.Count)
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Filter not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetSwitchFilterItem().Filter = profile.FilterWheelSettings.FilterWheelFilters[filterId];
                        }
                    }

                    if (!HttpContext.IsParameterOmitted(nameof(binning)))
                    {
                        if (!AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).Any())
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Binning not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetExposureItem().Binning = AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).First();
                        }
                    }

                    if ((gain < 0 || gain > AdvancedAPI.Controls.Camera.GetInfo().GainMax) && !HttpContext.IsParameterOmitted(nameof(gain)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid gain", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if ((offset < 0 || offset > AdvancedAPI.Controls.Camera.GetInfo().OffsetMax) && !HttpContext.IsParameterOmitted(nameof(offset)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid offset", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if (flats.Validate())
                    {
                        container = flats;
                        flatCancellationToken = new CancellationTokenSource();
                        var progress = AdvancedAPI.Controls.StatusMediator.GetStatus();
                        var token = flatCancellationToken.Token;
                        if (darkCount > 0)
                        {
                            flatTask = Task.Run(async () =>
                            {
                                await flats.Execute(progress, token);
                                if (!token.IsCancellationRequested)
                                {
                                    // waits for the user: after 5 min it answered OK on its own, and the dark flats were
                                    // taken with the scope open; cancelling the flat run ends it
                                    var result = await MyMessageBoxBroadcaster.Instance.ShowMessageBoxAsync(
                                        Loc.Instance["LblCoverScopeMsgBox"],
                                        Loc.Instance["LblCoverScopeMsgBoxTitle"],
                                        MessageBoxButton.OKCancel,
                                        MessageBoxResult.OK,
                                        Timeout.InfiniteTimeSpan,
                                        token);
                                    if (result != MessageBoxResult.OK || token.IsCancellationRequested)
                                        return;

                                    var darks = BuildDarkFlatsContainer(flats, darkCount, keepClosed);
                                    container = darks;
                                    if (darks.Validate())
                                        await darks.Execute(progress, token);
                                }
                            });
                        }
                        else
                        {
                            flatTask = flats.Execute(progress, token);
                        }
                        response.Response = "Process started";
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Issues found", 400));
                        response.Response = flats.Issues;
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

        [Route(HttpVerbs.Get, "/flats/trained-dark-flat")]
        public void TrainedDarkFlat([QueryField] int count,
                                    [QueryField] int filterId,
                                    [QueryField] string binning,
                                    [QueryField] int gain,
                                    [QueryField] int offset,
                                    [QueryField] bool keepClosed)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!flatTask?.IsCompleted ?? false)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Process already running", 400));
                }
                else if (RefuseWhileSequenceRuns() is { } refused)
                {
                    response = refused;
                }
                else
                {
                    TrainedDarkFlatExposure flats = new TrainedDarkFlatExposure(AdvancedAPI.Controls.Profile,
                                                                AdvancedAPI.Controls.Camera,
                                                                AdvancedAPI.Controls.Imaging,
                                                                AdvancedAPI.Controls.ImageSaveMediator,
                                                                AdvancedAPI.Controls.ImageHistory,
                                                                AdvancedAPI.Controls.FilterWheel,
                                                                AdvancedAPI.Controls.FlatDevice);

                    flats.GetIterations().Iterations = count;
                    flats.GetExposureItem().Gain = HttpContext.IsParameterOmitted(nameof(gain)) ? flats.GetExposureItem().Gain : gain;
                    flats.GetExposureItem().Offset = HttpContext.IsParameterOmitted(nameof(offset)) ? flats.GetExposureItem().Offset : offset;
                    flats.KeepPanelClosed = HttpContext.IsParameterOmitted(nameof(keepClosed)) ? flats.KeepPanelClosed : keepClosed;

                    IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;
                    FilterInfo filter = null;
                    if (!HttpContext.IsParameterOmitted(nameof(filterId)))
                    {
                        if (filterId < 0 || filterId >= profile.FilterWheelSettings.FilterWheelFilters.Count)
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Filter not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            filter = profile.FilterWheelSettings.FilterWheelFilters[filterId];
                            flats.GetSwitchFilterItem().Filter = filter;
                        }
                    }

                    if (!HttpContext.IsParameterOmitted(nameof(binning)))
                    {
                        if (!AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).Any())
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Binning not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetExposureItem().Binning = AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).First();
                        }
                    }

                    if ((gain < 0 || gain > AdvancedAPI.Controls.Camera.GetInfo().GainMax) && !HttpContext.IsParameterOmitted(nameof(gain)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid gain", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if ((offset < 0 || offset > AdvancedAPI.Controls.Camera.GetInfo().OffsetMax) && !HttpContext.IsParameterOmitted(nameof(offset)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid offset", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if (flats.Validate())
                    {
                        container = flats;
                        flatCancellationToken = new CancellationTokenSource();
                        var progress = AdvancedAPI.Controls.StatusMediator.GetStatus();
                        var token = flatCancellationToken.Token;
                        flatTask = Task.Run(async () =>
                        {
                            // TrainedDarkFlatExposure intentionally skips its own SwitchFilter step (it only
                            // uses the filter for the trained-exposure-time lookup), so without physically
                            // moving the wheel here first, the FITS header/filename would be tagged with
                            // whatever filter the wheel was last actually left on.
                            if (filter != null)
                            {
                                await AdvancedAPI.Controls.FilterWheel.ChangeFilter(filter, token, progress);
                            }
                            await flats.Execute(progress, token);
                        });
                        response.Response = "Process started";
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Issues found", 400));
                        response.Response = flats.Issues;
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

        [Route(HttpVerbs.Get, "/flats/trained-flat")]
        public void TrainedFlat([QueryField] int count,
                                [QueryField] int filterId,
                                [QueryField] string binning,
                                [QueryField] int gain,
                                [QueryField] int offset,
                                [QueryField] bool keepClosed)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!flatTask?.IsCompleted ?? false)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Process already running", 400));
                    HttpContext.WriteToResponse(response);
                    return;
                }

                if (RefuseWhileSequenceRuns() is { } refused)
                {
                    HttpContext.WriteToResponse(refused);
                    return;
                }
                if (!AdvancedAPI.Controls.Camera.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Camera not connected", 400));
                }
                else
                {
                    TrainedFlatExposure flats = new TrainedFlatExposure(AdvancedAPI.Controls.Profile,
                                                                        AdvancedAPI.Controls.Camera,
                                                                        AdvancedAPI.Controls.Imaging,
                                                                        AdvancedAPI.Controls.ImageSaveMediator,
                                                                        AdvancedAPI.Controls.ImageHistory,
                                                                        AdvancedAPI.Controls.FilterWheel,
                                                                        AdvancedAPI.Controls.FlatDevice);

                    flats.GetIterations().Iterations = count;
                    flats.GetExposureItem().Gain = HttpContext.IsParameterOmitted(nameof(gain)) ? flats.GetExposureItem().Gain : gain;
                    flats.GetExposureItem().Offset = HttpContext.IsParameterOmitted(nameof(offset)) ? flats.GetExposureItem().Offset : offset;
                    flats.KeepPanelClosed = HttpContext.IsParameterOmitted(nameof(keepClosed)) ? flats.KeepPanelClosed : keepClosed;

                    IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;
                    if (!HttpContext.IsParameterOmitted(nameof(filterId)))
                    {
                        if (filterId < 0 || filterId >= profile.FilterWheelSettings.FilterWheelFilters.Count)
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Filter not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetSwitchFilterItem().Filter = profile.FilterWheelSettings.FilterWheelFilters[filterId];
                        }
                    }

                    if (!HttpContext.IsParameterOmitted(nameof(binning)))
                    {
                        if (!AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).Any())
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Binning not available", 400));
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        else
                        {
                            flats.GetExposureItem().Binning = AdvancedAPI.Controls.Camera.GetInfo().BinningModes.Where(b => b.Name == binning).First();
                        }
                    }

                    if ((gain < 0 || gain > AdvancedAPI.Controls.Camera.GetInfo().GainMax) && !HttpContext.IsParameterOmitted(nameof(gain)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid gain", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if ((offset < 0 || offset > AdvancedAPI.Controls.Camera.GetInfo().OffsetMax) && !HttpContext.IsParameterOmitted(nameof(offset)))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Invalid offset", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    if (flats.Validate())
                    {
                        container = flats;
                        flatCancellationToken = new CancellationTokenSource();
                        flatTask = flats.Execute(AdvancedAPI.Controls.StatusMediator.GetStatus(), flatCancellationToken.Token);
                        response.Response = "Process started";
                    }
                    else
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Issues found", 400));
                        response.Response = flats.Issues;
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

        [Route(HttpVerbs.Get, "/flats/trained-settings")]
        public void GetTrainedFlatSettings()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                response.Response = AdvancedAPI.Controls.Profile.ActiveProfile.FlatDeviceSettings.TrainedFlatExposureSettings;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }
            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/flats/add-trained-setting")]
        public void AddTrainedFlatSetting()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                AdvancedAPI.Controls.Profile.ActiveProfile.FlatDeviceSettings.AddEmptyTrainedExposureSetting();
                response.Response = "Setting added";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }
            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/flats/update-trained-setting")]
        public void UpdateTrainedFlatSetting([QueryField] int index,
                                             [QueryField] int filterId,
                                             [QueryField] string binning,
                                             [QueryField] int gain,
                                             [QueryField] int offset,
                                             [QueryField] int brightness,
                                             [QueryField] double time)
        {
            HttpResponse response = new HttpResponse();
            try
            {
                var settings = AdvancedAPI.Controls.Profile.ActiveProfile.FlatDeviceSettings.TrainedFlatExposureSettings;
                if (index < 0 || index >= settings.Count)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Index out of range", 400));
                    HttpContext.WriteToResponse(response);
                    return;
                }
                var setting = settings[index];
                if (!HttpContext.IsParameterOmitted(nameof(filterId)))
                    setting.Filter = (short)filterId;
                if (!HttpContext.IsParameterOmitted(nameof(binning)))
                {
                    string[] parts = binning.Split('x');
                    if (parts.Length == 2 && short.TryParse(parts[0], out short bx) && short.TryParse(parts[1], out short by))
                        setting.Binning = new BinningMode(bx, by);
                }
                if (!HttpContext.IsParameterOmitted(nameof(gain)))
                    setting.Gain = gain;
                if (!HttpContext.IsParameterOmitted(nameof(offset)))
                    setting.Offset = offset;
                if (!HttpContext.IsParameterOmitted(nameof(brightness)))
                    setting.Brightness = brightness;
                if (!HttpContext.IsParameterOmitted(nameof(time)))
                    setting.Time = time;
                response.Response = "Setting updated";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }
            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/flats/remove-trained-setting")]
        public void RemoveTrainedFlatSetting([QueryField] int index)
        {
            HttpResponse response = new HttpResponse();
            try
            {
                var settings = AdvancedAPI.Controls.Profile.ActiveProfile.FlatDeviceSettings.TrainedFlatExposureSettings;
                if (index < 0 || index >= settings.Count)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Index out of range", 400));
                    HttpContext.WriteToResponse(response);
                    return;
                }
                var setting = settings[index];
                AdvancedAPI.Controls.Profile.ActiveProfile.FlatDeviceSettings.RemoveFlatExposureSetting(setting);
                response.Response = "Setting removed";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }
            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/flats/status")]
        public void FlatsStatus()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                response.Response = new FlatStatusResponse(container, flatTask);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/flats/stop")]
        public void FlatsStop()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (flatTask is not null && flatCancellationToken is not null)
                {
                    flatCancellationToken.Cancel();
                    response.Response = "Process stopped";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("No process running", 400));
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

    public struct FlatStatusResponse
    {
        public string State { get; }

        /// <summary>Why the run failed (State "Failed"), null otherwise.</summary>
        public string Error { get; }

        public string Type { get; }
        public int TotalIterations { get; }
        public int CompletedIterations { get; }
        public double? CurrentADU { get; }

        /// <summary>
        /// Bit depth of the image CurrentADU was measured on; its full scale is 2^BitDepth - 1.
        /// This is the converted image's bit depth, which for RAW frames can be below the camera's
        /// profile bit depth (e.g. 14 for a NEF while the profile says 16). Null while no ADU is known.
        /// </summary>
        public int? BitDepth { get; }

        public FlatStatusResponse(SequentialContainer container, Task task)
        {
            // a failed or stopped run reported "Finished" like a complete one
            Error = null;
            if (task is null)
            {
                State = "Finished";
            }
            else if (!task.IsCompleted)
            {
                State = "Running";
            }
            else if (task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException)
            {
                State = "Cancelled";
            }
            else if (task.IsFaulted)
            {
                State = "Failed";
                Error = task.Exception.GetBaseException().Message;
            }
            else
            {
                State = "Finished";
            }

            Type = container is TrainedDarkFlatExposure ? "darks" : "flats";

            if (State.Equals("Running"))
            {
                LoopCondition loop = (LoopCondition)container.GetType().GetMethod("GetIterations").Invoke(container, null);
                TotalIterations = loop.Iterations;
                CompletedIterations = loop.CompletedIterations;
            }
            else
            {
                TotalIterations = -1;
                CompletedIterations = -1;
            }

            double rawADU = 0;
            if (container is AutoExposureFlat aef) rawADU = aef.DeterminedHistogramADU;
            else if (container is AutoBrightnessFlat abf) rawADU = abf.DeterminedHistogramADU;
            else if (container is SkyFlat sf) rawADU = sf.DeterminedHistogramADU;
            CurrentADU = rawADU > 0 ? rawADU : (double?)null;

            // The flat items prepare every measured frame before reading its statistics, so the last
            // prepared image is the one DeterminedHistogramADU came from.
            BitDepth = null;
            if (CurrentADU is not null)
            {
                lock (ImageWatcher.imageLock)
                {
                    BitDepth = ImageWatcher.PreparedImage?.RawImageData?.Properties?.BitDepth;
                }
            }
        }
    }
}
