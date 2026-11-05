#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using ninaAPI.Utility;

namespace ninaAPI.WebService.V2
{
    public class HistogramBucket
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class PreparedImageStatistics
    {
        public double StDev { get; set; }
        public double Mean { get; set; }
        public double Median { get; set; }
        public double MedianAbsoluteDeviation { get; set; }
        public int Min { get; set; }
        public int Max { get; set; }
        public int BitDepth { get; set; }
        public List<HistogramBucket> Histogram { get; set; }
    }

    public class ImageResponse
    {
        public double ExposureTime { get; set; }
        public string ImageType { get; set; }
        public string Filter { get; set; }
        public string RmsText { get; set; }
        public double Temperature { get; set; }
        public string CameraName { get; set; }
        public string TargetName { get; set; }
        public int Gain { get; set; }
        public int Offset { get; set; }
        public DateTime Date { get; set; }
        public string TelescopeName { get; set; }
        public double FocalLength { get; set; }
        public double StDev { get; set; }
        public double Mean { get; set; }
        public double Median { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public int Stars { get; set; }
        public double HFR { get; set; }
        public double HFRStDev { get; set; }
        public bool IsBayered { get; set; }
        // Identity of the entry, assigned when the image was saved.
        public string Id { get; set; }
        // Set by the delete action. The entry itself stays in the history because
        // indices and thumbnails are positional; clients hide flagged entries.
        public bool IsDeleted { get; set; }
        public int? FocuserPosition { get; set; }
        public string Filename { get => Path?.IsFile == true ? System.IO.Path.GetFileName(Path.LocalPath) : null; }

        private Uri Path { get; set; }

        private ImageResponse() { }

        // Null-safe: the event can come without statistics, star analysis or recorded RMS, and
        // what is missing reads as 0 or null rather than failing the whole event.
        public static ImageResponse FromEvent(ImageSavedEventArgs e)
        {
            return new ImageResponse()
            {
                Id = Guid.NewGuid().ToString("N"),
                ExposureTime = e.Duration,
                TargetName = e.MetaData?.Target?.Name,
                ImageType = e.MetaData?.Image?.ImageType,
                Filter = e.Filter,
                RmsText = e.MetaData?.Image?.RecordedRMS?.TotalText,
                Temperature = e.MetaData?.Camera?.Temperature ?? 0,
                CameraName = e.MetaData?.Camera?.Name,
                Gain = e.MetaData?.Camera?.Gain ?? 0,
                Offset = e.MetaData?.Camera?.Offset ?? 0,
                Date = DateTime.Now,
                TelescopeName = e.MetaData?.Telescope?.Name,
                FocalLength = e.MetaData?.Telescope?.FocalLength ?? 0,
                StDev = e.Statistics?.StDev ?? 0,
                Mean = e.Statistics?.Mean ?? 0,
                Median = e.Statistics?.Median ?? 0,
                Stars = e.StarDetectionAnalysis?.DetectedStars ?? 0,
                HFR = e.StarDetectionAnalysis?.HFR ?? 0,
                HFRStDev = e.StarDetectionAnalysis?.HFRStDev ?? 0,
                Min = e.Statistics?.Min ?? 0,
                Max = e.Statistics?.Max ?? 0,
                Path = e.PathToImage,
                IsBayered = e.IsBayered,
                FocuserPosition = e.MetaData?.Focuser?.Position,
            };
        }

        public string GetPath()
        {
            return Path.LocalPath;
        }

        public void SetPath(string path)
        {
            Path = new Uri(path);
        }
    }

    public class ImageEvent(ImageResponse stats)
    {
        public string Event { get; set; } = "IMAGE-SAVE";
        public ImageResponse ImageStatistics { get; set; } = stats;
    }

    public class PlateSolveResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
        public double? RA { get; set; }
        public double? Dec { get; set; }
        public string RAString { get; set; }
        public string DecString { get; set; }
        public double? Orientation { get; set; }
        public double? PixelScale { get; set; }
        public double? Radius { get; set; }
    }

    public class PlateSolveState
    {
        public bool IsSolving { get; set; }
        public dynamic CurrentResult { get; set; }
        public DateTime LastSolveTime { get; set; }
    }

    public class ImageWatcher : INinaWatcher
    {
        public static List<ImageResponse> Images = new List<ImageResponse>();
        public static List<KeyValuePair<int, string>> Thumbnails = new List<KeyValuePair<int, string>>();
        public static IRenderedImage PreparedImage { get; private set; }
        public static PlateSolveState PlateSolveState { get; private set; } = new PlateSolveState();

        public static object imageLock = new object();

        public void StartWatchers()
        {
            AdvancedAPI.Controls.ImageSaveMediator.ImageSaved += ImageSaved;
            AdvancedAPI.Controls.Imaging.ImagePrepared += ImagePrepared;
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.ImageSaveMediator.ImageSaved -= ImageSaved;
            AdvancedAPI.Controls.Imaging.ImagePrepared -= ImagePrepared;
        }

        // The image handlers are async void: an exception escaping them ends the process when there
        // is no synchronization context (headless pins), so they log instead.
        private static async void ImagePrepared(object sender, ImagePreparedEventArgs e)
        {
            try
            {
                lock (imageLock)
                {
                    PreparedImage = e.RenderedImage;
                }

                HttpResponse response = new HttpResponse() { Type = HttpResponse.TypeSocket };

                response.Response = new Dictionary<string, object>() { { "Event", "IMAGE-PREPARED" } };

                await WebSocketV2.SendEvent(response);
            }
            catch (Exception ex)
            {
                Logger.Error("IMAGE-PREPARED event failed", ex);
            }
        }

        // Keyed by the image's position in Images: a thumbnail that was skipped (no rendered image, a failed write)
        // shifted every later key by one when they were counted, so /image/thumbnail/{i} served another image's
        // thumbnail for the rest of the session and none for the newest.
        private static void CacheThumbnail(ImageSavedEventArgs e, ImageResponse image)
        {
            if (!Properties.Settings.Default.CreateThumbnails)
                return;
            if (e.Image == null)
            {
                Logger.Warning("No thumbnail for the saved image: the event carries no rendered image");
                return;
            }
            lock (imageLock)
            {
                int index = Images.IndexOf(image);
                if (index < 0)
                {
                    return;
                }

                string thumbnailFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), $"thumbnails-{Environment.ProcessId}");
                Directory.CreateDirectory(thumbnailFile);
                thumbnailFile = Path.Combine(thumbnailFile, $"{index}.jpg");
                var img = BitmapHelper.ScaleBitmap(e.Image, 256 / e.Image.Width);

                JpegBitmapEncoder encoder = new JpegBitmapEncoder();
                encoder.QualityLevel = 100;
                encoder.Frames.Add(BitmapFrame.Create(img));

                using (FileStream fs = new FileStream(thumbnailFile, FileMode.Create))
                {
                    encoder.Save(fs);
                }

                Thumbnails.Add(new KeyValuePair<int, string>(index, thumbnailFile));
            }
        }

        private static async void ImageSaved(object sender, ImageSavedEventArgs e)
        {
            try
            {
                HttpResponse response = new HttpResponse() { Type = HttpResponse.TypeSocket };

                var r = ImageResponse.FromEvent(e);

                response.Response = new ImageEvent(r);

                HttpResponse imageEvent = new HttpResponse() { Type = HttpResponse.TypeSocket, Response = new Dictionary<string, object>() { { "Event", "IMAGE-SAVE" }, { "Time", DateTime.Now } } };

                lock (imageLock)
                {
                    Images.Add(r);
                }

                WebSocketV2.AddEvent(imageEvent);

                // A thumbnail that can't be written (full or read-only storage) must not cost the event.
                try
                {
                    CacheThumbnail(e, r);
                }
                catch (Exception ex)
                {
                    Logger.Error("Writing the thumbnail of the saved image failed", ex);
                }

                await WebSocketV2.SendEvent(response);
            }
            catch (Exception ex)
            {
                Logger.Error("IMAGE-SAVE event failed", ex);
            }
        }
    }

    public partial class ControllerV2
    {
        [Route(HttpVerbs.Get, "/prepared-image")]
        public async Task GetPreparedImage([QueryField] bool resize,
                    [QueryField] int quality,
                    [QueryField] string size,
                    [QueryField] double scale,
                    [QueryField] double factor,
                    [QueryField] double blackClipping,
                    [QueryField] bool unlinked,
                    [QueryField] bool debayer,
                    [QueryField] bool autoPrepare,
                    [QueryField] string bayerPattern)
        {
            HttpResponse response = new HttpResponse();
            IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;

            SensorType sensor = SensorType.Monochrome;

            if (HttpContext.IsParameterOmitted(nameof(factor)) || autoPrepare)
            {
                factor = profile.ImageSettings.AutoStretchFactor;
            }
            if (HttpContext.IsParameterOmitted(nameof(blackClipping)) || autoPrepare)
            {
                blackClipping = profile.ImageSettings.BlackClipping;
            }
            if (HttpContext.IsParameterOmitted(nameof(unlinked)) || autoPrepare)
            {
                unlinked = profile.ImageSettings.UnlinkedStretch;
            }
            if (HttpContext.IsParameterOmitted(nameof(bayerPattern)) || autoPrepare)
            {
                sensor = DebayerPattern.SensorTypeFor(profile.CameraSettings.BayerPattern, AdvancedAPI.Controls.Camera.GetInfo());
            }
            else if (!DebayerPattern.TryParse(bayerPattern, out sensor))
            {
                response = CoreUtility.CreateErrorTable(new Error("Invalid bayer pattern", 400));
                HttpContext.WriteToResponse(response);
                return;
            }

            quality = Math.Clamp(quality, -1, 100);
            if (quality == 0)
                quality = -1; // quality should be set to -1 for png if omitted

            if (resize && string.IsNullOrWhiteSpace(size)) // workaround as default parameters are not working
                size = "640x480";

            try
            {
                Size sz = Size.Empty;
                if (resize)
                {
                    string[] s = size.Split('x');
                    int width = int.Parse(s[0]);
                    int height = int.Parse(s[1]);
                    sz = new Size(width, height);
                }

                IRenderedImage renderedImage = ImageWatcher.PreparedImage;

                if (renderedImage is null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No image", 404));
                    HttpContext.WriteToResponse(response);
                    return;
                }

                // NINA already debayered the prepared image when the profile's DebayerImage is on; debayering it again garbles it
                if (renderedImage is not IDebayeredImage
                    && DebayerPattern.CanDebayer(sensor)
                    && (debayer || (autoPrepare && renderedImage.RawImageData.Properties.IsBayered)))
                {
                    try
                    {
                        renderedImage = renderedImage.Debayer(bayerPattern: sensor, saveColorChannels: true, saveLumChannel: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex);
                    }
                }

                // NINA keeps the colour channels of its debayered image only for its profile's unlinked stretch;
                // an unlinked stretch of the empty channels renders white
                if (unlinked && renderedImage is IDebayeredImage debayeredImage && debayeredImage.DebayeredData?.Red is not { Length: > 0 })
                {
                    Logger.Debug("Prepared image: NINA kept no colour channels, so stretching linked instead of unlinked");
                    unlinked = false;
                }

                renderedImage = await renderedImage.Stretch(factor, blackClipping, unlinked);

                BitmapEncoder encoder = null;
                if (scale == 0 && resize)
                {
                    BitmapSource image = BitmapHelper.ResizeBitmap(renderedImage.Image, sz);
                    encoder = BitmapHelper.GetEncoder(image, quality);
                }
                if (scale != 0 && resize)
                {
                    BitmapSource image = BitmapHelper.ScaleBitmap(renderedImage.Image, scale);
                    encoder = BitmapHelper.GetEncoder(image, quality);
                }
                if (!resize)
                {
                    BitmapSource image = BitmapHelper.ScaleBitmap(renderedImage.Image, 1);
                    encoder = BitmapHelper.GetEncoder(image, quality);
                }
                HttpContext.Response.ContentType = quality == -1 ? "image/png" : "image/jpeg";
                using (MemoryStream memory = new MemoryStream())
                {
                    encoder.Save(memory);
                    await HttpContext.Response.OutputStream.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length));
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/platesolve")]
        public void PlateSolveCurrentImage()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                // Get the current prepared image
                IRenderedImage renderedImage = null;
                lock (ImageWatcher.imageLock)
                {
                    renderedImage = ImageWatcher.PreparedImage;
                }

                if (renderedImage == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No image available to plate solve", 404));
                    HttpContext.WriteToResponse(response);
                    return;
                }

                // Check if already solving
                if (ImageWatcher.PlateSolveState.IsSolving)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Plate solve already in progress", 400));
                    HttpContext.WriteToResponse(response);
                    return;
                }

                // Perform plate solve synchronously
                var psResult = PerformPlateSolveSync(renderedImage);

                // Return result directly
                response.Response = psResult;
            }
            catch (Exception ex)
            {
                Logger.Error("Error during plate solve", ex);
                response = CoreUtility.CreateErrorTable(new Error(ex.Message, 500));
            }

            HttpContext.WriteToResponse(response);
        }

        private static PlateSolveResponse PerformPlateSolveSync(IRenderedImage renderedImage)
        {
            try
            {
                ImageWatcher.PlateSolveState.IsSolving = true;

                var plateSolver = AdvancedAPI.Controls.PlateSolver.GetPlateSolver(AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings);
                var blindSolver = AdvancedAPI.Controls.PlateSolver.GetBlindSolver(AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings);

                var parameter = new PlateSolveParameter()
                {
                    Binning = AdvancedAPI.Controls.Camera.GetInfo()?.BinX ?? 1,
                    Coordinates = AdvancedAPI.Controls.Mount.GetCurrentPosition(),
                    DownSampleFactor = AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings.DownSampleFactor,
                    FocalLength = AdvancedAPI.Controls.Profile.ActiveProfile.TelescopeSettings.FocalLength,
                    MaxObjects = AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings.MaxObjects,
                    PixelSize = AdvancedAPI.Controls.Profile.ActiveProfile.CameraSettings.PixelSize,
                    Regions = AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings.Regions,
                    SearchRadius = AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings.SearchRadius,
                    BlindFailoverEnabled = AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings.BlindFailoverEnabled
                };

                var imageSolver = new ImageSolver(plateSolver, blindSolver);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    var result = imageSolver.Solve(renderedImage.RawImageData, parameter, null, cts.Token).GetAwaiter().GetResult();

                    if (result.Success && AdvancedAPI.Controls.Mount.GetInfo().Connected)
                    {
                        var scopePosition = AdvancedAPI.Controls.Mount.GetCurrentPosition();
                        var resultCoordinates = result.Coordinates.Transform(scopePosition.Epoch);
                        result.Separation = scopePosition - resultCoordinates;
                    }

                    lock (ImageWatcher.imageLock)
                    {
                        ImageWatcher.PlateSolveState.CurrentResult = result;
                        ImageWatcher.PlateSolveState.LastSolveTime = DateTime.Now;
                    }

                    // Format and return result
                    return FormatPlateSolveResult(result);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warning("Plate solve timed out after 60 seconds");
                return new PlateSolveResponse
                {
                    Success = false,
                    Message = "Plate solve timed out after 60 seconds",
                    Error = "Timeout"
                };
            }
            catch (Exception ex)
            {
                Logger.Error("Error during plate solve", ex);
                return new PlateSolveResponse
                {
                    Success = false,
                    Message = "Plate solve failed: " + ex.Message,
                    Error = ex.Message
                };
            }
            finally
            {
                ImageWatcher.PlateSolveState.IsSolving = false;
            }
        }

        private static PlateSolveResponse FormatPlateSolveResult(dynamic psResult)
        {
            try
            {
                // Extract values from the PlateSolveResult object using reflection
                var resultType = psResult.GetType();
                var successProp = resultType.GetProperty("Success");
                var coordinatesProp = resultType.GetProperty("Coordinates");
                var positionAngleProp = resultType.GetProperty("PositionAngle");
                var pixelScaleProp = resultType.GetProperty("PixelScale");
                var radiusProp = resultType.GetProperty("Radius");

                var success = (bool?)(successProp?.GetValue(psResult)) ?? false;
                var coordinates = coordinatesProp?.GetValue(psResult);
                var positionAngle = (double?)(positionAngleProp?.GetValue(psResult));
                var pixelScale = (double?)(pixelScaleProp?.GetValue(psResult));
                var radius = (double?)(radiusProp?.GetValue(psResult));

                double? ra = null;
                double? dec = null;
                string raString = null;
                string decString = null;

                if (coordinates != null)
                {
                    var coordType = coordinates.GetType();
                    var raDegreesProperty = coordType.GetProperty("RADegrees");
                    var decProperty = coordType.GetProperty("Dec");

                    ra = (double?)(raDegreesProperty?.GetValue(coordinates));
                    dec = (double?)(decProperty?.GetValue(coordinates));

                    // Convert RA from degrees to hours and format as string
                    if (ra.HasValue)
                    {
                        var raHours = ra.Value / 15.0; // Convert degrees to hours (360 degrees = 24 hours)
                        var hours = (int)raHours;
                        var minutes = (int)((raHours - hours) * 60);
                        var seconds = ((raHours - hours) * 60 - minutes) * 60;
                        raString = $"{hours:D2}h{minutes:D2}m{seconds:F2}s";
                    }

                    // Format Dec as string
                    if (dec.HasValue)
                    {
                        var decAbs = Math.Abs(dec.Value);
                        var decDegrees = (int)decAbs;
                        var decMinutes = (int)((decAbs - decDegrees) * 60);
                        var decSeconds = ((decAbs - decDegrees) * 60 - decMinutes) * 60;
                        var sign = dec.Value < 0 ? "-" : "+";
                        decString = $"{sign}{decDegrees:D2}°{decMinutes:D2}'{decSeconds:F2}\"";
                    }
                }

                return new PlateSolveResponse
                {
                    Success = success,
                    Message = success ? "Plate solve successful" : "Plate solve failed",
                    RA = ra,
                    Dec = dec,
                    RAString = raString,
                    DecString = decString,
                    Orientation = positionAngle,
                    PixelScale = pixelScale,
                    Radius = radius
                };
            }
            catch (Exception ex)
            {
                Logger.Error("Error formatting plate solve result", ex);
                return new PlateSolveResponse
                {
                    Success = false,
                    Message = "Error formatting result: " + ex.Message
                };
            }
        }

        [Route(HttpVerbs.Get, "/platesolve/cancel")]
        public void CancelPlateSolve()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                lock (ImageWatcher.imageLock)
                {
                    ImageWatcher.PlateSolveState.IsSolving = false;
                }

                response.Response = new PlateSolveResponse
                {
                    Success = true,
                    Message = "Plate solve cancelled"
                };
            }
            catch (Exception ex)
            {
                Logger.Error("Error cancelling plate solve", ex);
                response = CoreUtility.CreateErrorTable(new Error(ex.Message, 500));
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/image/{index}")]
        public async Task GetImage(int index,
                    [QueryField] bool resize,
                    [QueryField] int quality,
                    [QueryField] string size,
                    [QueryField] double scale,
                    [QueryField] double factor,
                    [QueryField] double blackClipping,
                    [QueryField] bool unlinked,
                    [QueryField] bool stream,
                    [QueryField] bool debayer,
                    [QueryField] string bayerPattern,
                    [QueryField] bool autoPrepare,
                    [QueryField] string imageType,
                    [QueryField] bool raw_fits)
        {
            HttpResponse response = new HttpResponse();
            IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;

            SensorType sensor = SensorType.Monochrome;

            if (HttpContext.IsParameterOmitted(nameof(factor)) || autoPrepare)
            {
                factor = profile.ImageSettings.AutoStretchFactor;
            }
            if (HttpContext.IsParameterOmitted(nameof(blackClipping)) || autoPrepare)
            {
                blackClipping = profile.ImageSettings.BlackClipping;
            }
            if (HttpContext.IsParameterOmitted(nameof(unlinked)) || autoPrepare)
            {
                unlinked = profile.ImageSettings.UnlinkedStretch;
            }
            if (HttpContext.IsParameterOmitted(nameof(bayerPattern)) || autoPrepare)
            {
                sensor = DebayerPattern.SensorTypeFor(profile.CameraSettings.BayerPattern, AdvancedAPI.Controls.Camera.GetInfo());
            }
            else if (!DebayerPattern.TryParse(bayerPattern, out sensor))
            {
                response = CoreUtility.CreateErrorTable(new Error("Invalid bayer pattern", 400));
                HttpContext.WriteToResponse(response);
                return;
            }

            quality = Math.Clamp(quality, -1, 100);
            if (quality == 0)
                quality = -1; // quality should be set to -1 for png if omitted

            if (resize && string.IsNullOrWhiteSpace(size)) // workaround as default parameters are not working
                size = "640x480";

            try
            {
                Size sz = Size.Empty;
                if (resize)
                {
                    string[] s = size.Split('x');
                    int width = int.Parse(s[0]);
                    int height = int.Parse(s[1]);
                    sz = new Size(width, height);
                }

                IEnumerable<ImageResponse> points;
                lock (ImageWatcher.imageLock)
                {
                    points = HttpContext.IsParameterOmitted(nameof(imageType)) ? ImageWatcher.Images : ImageWatcher.Images.Where(x => x.ImageType.Equals(imageType));
                }

                if (!points.Any())
                {
                    response = CoreUtility.CreateErrorTable(new Error("No images available", 500));
                }
                else if (index >= points.Count() || index < 0)
                {
                    response = CoreUtility.CreateErrorTable(CommonErrors.INDEX_OUT_OF_RANGE);
                }
                else
                {
                    ImageResponse p = points.ElementAt(index); // Get the history point at the specified index for the image

                    if (p.IsDeleted)
                    {
                        response = CoreUtility.CreateErrorTable(CommonErrors.IMAGE_DELETED);
                        HttpContext.WriteToResponse(response);
                        return;
                    }

                    IImageData imageData = await Retry.Do(async () => await AdvancedAPI.Controls.ImageDataFactory.CreateFromFile(p.GetPath(), 16, p.IsBayered, RawConverterEnum.FREEIMAGE), TimeSpan.FromMilliseconds(200), 10);

                    if (HttpContext.IsParameterOmitted(nameof(raw_fits)))
                    {
                        IRenderedImage renderedImage = imageData.RenderImage();

                        if (DebayerPattern.CanDebayer(sensor)
                            && (debayer || (autoPrepare && renderedImage.RawImageData.Properties.IsBayered)))
                        {
                            renderedImage = renderedImage.Debayer(bayerPattern: sensor, saveColorChannels: true, saveLumChannel: true);
                        }
                        renderedImage = await renderedImage.Stretch(factor, blackClipping, unlinked);


                        if (stream)
                        {
                            BitmapEncoder encoder = null;
                            if (scale == 0 && resize)
                            {
                                BitmapSource image = BitmapHelper.ResizeBitmap(renderedImage.Image, sz);
                                encoder = BitmapHelper.GetEncoder(image, quality);
                            }
                            if (scale != 0 && resize)
                            {
                                BitmapSource image = BitmapHelper.ScaleBitmap(renderedImage.Image, scale);
                                encoder = BitmapHelper.GetEncoder(image, quality);
                            }
                            if (!resize)
                            {
                                BitmapSource image = BitmapHelper.ScaleBitmap(renderedImage.Image, 1);
                                encoder = BitmapHelper.GetEncoder(image, quality);
                            }
                            HttpContext.Response.ContentType = quality == -1 ? "image/png" : "image/jpeg";
                            using (MemoryStream memory = new MemoryStream())
                            {
                                encoder.Save(memory);
                                await HttpContext.Response.OutputStream.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length));
                                return;
                            }
                        }
                        else
                        {

                            if (scale == 0 && resize)
                                response.Response = BitmapHelper.ResizeAndConvertBitmap(renderedImage.Image, sz, quality);
                            if (scale != 0 && resize)
                                response.Response = BitmapHelper.ScaleAndConvertBitmap(renderedImage.Image, scale, quality);
                            if (!resize)
                                response.Response = BitmapHelper.ScaleAndConvertBitmap(renderedImage.Image, 1, quality);
                        }
                    }
                    else
                    {
                        // If the image is not of type FITS return an error.
                        if (!p.GetPath().EndsWith(".fits", true, null))
                        {
                            response = CoreUtility.CreateErrorTable(new Error("Image is not a FITS file", 400));
                        }
                        else
                        {
                            // Create the FITS image.
                            FITS f = new FITS(
                                imageData.Data.FlatArray,
                                imageData.Properties.Width,
                                imageData.Properties.Height
                            );

                            f.PopulateHeaderCards(imageData.MetaData);

                            // Populate the stream with the raw byte data of the FITS image.
                            MemoryStream fits_stream = new MemoryStream();

                            f.Write(fits_stream);

                            // Encode the stream in base64 and send it.
                            response.Response = Convert.ToBase64String(fits_stream.ToArray());
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

        [Route(HttpVerbs.Get, "/prepared-image/statistics")]
        public async Task GetPreparedImageStatistics()
        {
            HttpResponse response = new HttpResponse();
            try
            {
                IRenderedImage renderedImage;
                lock (ImageWatcher.imageLock)
                {
                    renderedImage = ImageWatcher.PreparedImage;
                }

                if (renderedImage is null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No image", 404));
                    HttpContext.WriteToResponse(response);
                    return;
                }

                var statistics = await renderedImage.RawImageData.Statistics;
                response.Response = new PreparedImageStatistics
                {
                    StDev = statistics.StDev,
                    Mean = statistics.Mean,
                    Median = statistics.Median,
                    MedianAbsoluteDeviation = statistics.MedianAbsoluteDeviation,
                    Min = statistics.Min,
                    Max = statistics.Max,
                    BitDepth = statistics.BitDepth,
                    Histogram = statistics.Histogram?.Select(p => new HistogramBucket { X = p.X, Y = p.Y }).ToList(),
                };
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/prepared-image/solve")]
        public async Task SolvePreparedImage()
        {
            if (ImageWatcher.PreparedImage is null)
            {
                HttpContext.WriteToResponse(CoreUtility.CreateErrorTable(new Error("Image not available", 404)));
                return;
            }
            await SolveImage(-1, string.Empty, ImageWatcher.PreparedImage);
        }

        [Route(HttpVerbs.Get, "/image/{index}/solve")]
        public async Task SolveImage(int index, [QueryField] string imageType, object image = null)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IRenderedImage img = null;
                if (image is null)
                {
                    IEnumerable<ImageResponse> points;
                    lock (ImageWatcher.imageLock)
                    {
                        points = HttpContext.IsParameterOmitted(nameof(imageType)) ? ImageWatcher.Images : ImageWatcher.Images.Where(x => x.ImageType.Equals(imageType));
                    }

                    if (!points.Any())
                    {
                        response = CoreUtility.CreateErrorTable(new Error("No images available", 400));
                        HttpContext.WriteToResponse(response);
                        return;
                    }
                    else if (index >= points.Count() || index < 0)
                    {
                        response = CoreUtility.CreateErrorTable(CommonErrors.INDEX_OUT_OF_RANGE);
                        HttpContext.WriteToResponse(response);
                        return;
                    }
                    else
                    {
                        ImageResponse p = points.ElementAt(index);
                        if (p.IsDeleted)
                        {
                            response = CoreUtility.CreateErrorTable(CommonErrors.IMAGE_DELETED);
                            HttpContext.WriteToResponse(response);
                            return;
                        }
                        IImageData imageData = await Retry.Do(async () => await AdvancedAPI.Controls.ImageDataFactory.CreateFromFile(p.GetPath(), 16, p.IsBayered, RawConverterEnum.FREEIMAGE), TimeSpan.FromMilliseconds(200), 10);
                        img = imageData.RenderImage();
                    }
                }
                else
                {
                    img = (IRenderedImage)image;
                }

                plateSolveResult = null;
                IPlateSolveSettings settings = AdvancedAPI.Controls.Profile.ActiveProfile.PlateSolveSettings;

                IPlateSolverFactory platesolver = AdvancedAPI.Controls.PlateSolver;
                Coordinates coordinates = AdvancedAPI.Controls.Mount.GetCurrentPosition();
                double focalLength = AdvancedAPI.Controls.Profile.ActiveProfile.TelescopeSettings.FocalLength;
                CaptureSolverParameter solverParameter = new CaptureSolverParameter()
                {
                    Attempts = 1,
                    // the binning the image was taken with, not the plate solve profile's: a 2x2 image solved with the
                    // profile's 1x1 got half the image scale, and the solve failed or fell back to a slow blind solve
                    Binning = img.RawImageData.MetaData.Camera.BinX,
                    BlindFailoverEnabled = settings.BlindFailoverEnabled,
                    Coordinates = coordinates,
                    DownSampleFactor = settings.DownSampleFactor,
                    FocalLength = focalLength,
                    MaxObjects = settings.MaxObjects,
                    Regions = settings.Regions,
                    SearchRadius = settings.SearchRadius,
                    PixelSize = img.RawImageData.MetaData.Camera.PixelSize
                };
                IImageSolver captureSolver = platesolver.GetImageSolver(platesolver.GetPlateSolver(settings), platesolver.GetBlindSolver(settings));

                plateSolveResult = await captureSolver.Solve(img.RawImageData, solverParameter, AdvancedAPI.Controls.StatusMediator.GetStatus(), HttpContext.CancellationToken);

                response.Response = plateSolveResult;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);

        }

        [Route(HttpVerbs.Get, "/image/{index}/prefix")]
        public void AddPrefix(int index, [QueryField(true)] string prefix, [QueryField] string imageType)
        {
            HttpResponse response = new HttpResponse();

            lock (ImageWatcher.imageLock)
            {
                IEnumerable<ImageResponse> points = HttpContext.IsParameterOmitted(nameof(imageType)) ? ImageWatcher.Images : ImageWatcher.Images.Where(x => x.ImageType.Equals(imageType));

                if (!points.Any())
                {
                    response = CoreUtility.CreateErrorTable(new Error("No images available", 400));
                }
                else if (index >= points.Count() || index < 0)
                {
                    response = CoreUtility.CreateErrorTable(CommonErrors.INDEX_OUT_OF_RANGE);
                }
                else if (points.ElementAt(index).IsDeleted)
                {
                    response = CoreUtility.CreateErrorTable(CommonErrors.IMAGE_DELETED);
                }
                else
                {
                    ImageResponse p = points.ElementAt(index);
                    string newPath = Path.Join(Path.GetDirectoryName(p.GetPath()), prefix + Path.GetFileName(p.GetPath()));
                    if (File.Exists(newPath))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("File already exists", 400));
                    }
                    else if (!File.Exists(p.GetPath()))
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Image file does not exist", 400));
                    }
                    else
                    {
                        File.Move(p.GetPath(), newPath);
                        p.SetPath(newPath);
                        response.Response = "Image renamed with prefix " + prefix;
                    }
                }
            }

            HttpContext.WriteToResponse(response);
        }

        /// <summary>
        /// Deletes the image file behind a history entry from disk. The entry is addressed by
        /// its <see cref="ImageResponse.Id"/>, not by a positional index, so a history that was
        /// rebuilt or extended between listing and deleting can never point at another file.
        /// <paramref name="filename"/> is an optional cross-check: when given it has to match the
        /// entry's file name, otherwise nothing is deleted (409).
        /// </summary>
        [Route(HttpVerbs.Delete, "/image-history/{id}")]
        public void DeleteHistoryImage(string id, [QueryField] string filename)
        {
            HttpResponse response = new HttpResponse();

            lock (ImageWatcher.imageLock)
            {
                ImageResponse p = null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    p = ImageWatcher.Images.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
                }

                if (p == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Unknown image id", 404));
                }
                else if (!string.IsNullOrEmpty(filename) && !string.Equals(filename, p.Filename, StringComparison.Ordinal))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Image file name does not match", 409));
                }
                else if (p.IsDeleted)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Image already deleted", 400));
                }
                else if (!File.Exists(p.GetPath()))
                {
                    p.IsDeleted = true;
                    Logger.Info($"Image file {p.GetPath()} does not exist, marking as deleted");
                    response = CoreUtility.CreateErrorTable(new Error("Image file does not exist", 400));
                }
                else
                {
                    try
                    {
                        File.Delete(p.GetPath());
                        // The entry stays in the history: indices and thumbnails are positional,
                        // removing it would shift every later image.
                        p.IsDeleted = true;
                        Logger.Info($"Deleted image {p.GetPath()} on request");
                        response.Response = "Image deleted";
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex);
                        response = CoreUtility.CreateErrorTable(new Error(ex.Message, 500));
                    }
                }
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/image-history")]
        public void GetHistoryCount([QueryField] bool all, [QueryField] int index, [QueryField] bool count, [QueryField] string imageType)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var images = HttpContext.IsParameterOmitted(nameof(imageType)) ? ImageWatcher.Images : ImageWatcher.Images.Where(x => x.ImageType.Equals(imageType));
                index = HttpContext.IsParameterOmitted(nameof(index)) ? images.Count() - 1 : index;
                if (count)
                {
                    response.Response = images.Count();
                }
                else if (all)
                {
                    response.Response = images;
                }
                else if (index >= 0 && index < images.Count())
                {
                    List<object> result = [images.ElementAt(index)]; // TODO: put this directly into the response (maybe)
                    response.Response = result;
                }
                else if (index >= images.Count() || index < 0)
                {
                    response = CoreUtility.CreateErrorTable(CommonErrors.INDEX_OUT_OF_RANGE);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/image/thumbnail/{index}")]
        public async Task GetImage(int index,
                    [QueryField] string imageType)
        {
            HttpResponse response = new HttpResponse();
            IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;

            try
            {
                if (ImageWatcher.Thumbnails.Count == 0)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No thumbnails available", 400));
                }
                else
                {
                    string res;
                    lock (ImageWatcher.imageLock)
                    {
                        var images = HttpContext.IsParameterOmitted(nameof(imageType)) ? ImageWatcher.Images : ImageWatcher.Images.Where(x => x.ImageType.Equals(imageType));

                        ImageResponse image = images.ElementAt(index);
                        if (image.IsDeleted)
                        {
                            response = CoreUtility.CreateErrorTable(CommonErrors.IMAGE_DELETED);
                            HttpContext.WriteToResponse(response);
                            return;
                        }

                        var i = ImageWatcher.Images.IndexOf(image);
                        res = ImageWatcher.Thumbnails.Where(x => x.Key == i).Select(x => x.Value).FirstOrDefault();
                        if (res == null)
                        {
                            response = CoreUtility.CreateErrorTable(new Error("No thumbnail for this image", 404));
                            HttpContext.WriteToResponse(response);
                            return;
                        }

                        HttpContext.Response.ContentType = "image/jpeg";
                    }

                    using (FileStream fs = File.OpenRead(res))
                    {
                        await fs.CopyToAsync(HttpContext.Response.OutputStream);
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
    }
}
