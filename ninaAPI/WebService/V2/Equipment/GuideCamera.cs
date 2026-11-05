#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using ninaAPI.Utility;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ninaAPI.WebService.V2
{
    /// <summary>
    /// pins: websocket events of the guide camera slot, like CameraWatcher's for the imaging camera.
    /// </summary>
    public class GuideCameraWatcher : INinaWatcher, ICameraConsumer
    {
        private readonly Func<object, EventArgs, Task> ConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDECAMERA-CONNECTED");
        private readonly Func<object, EventArgs, Task> DisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDECAMERA-DISCONNECTED");
        private readonly Func<object, EventArgs, Task> DownloadTimeoutHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDECAMERA-DOWNLOAD-TIMEOUT");

        public void Dispose()
        {
            AdvancedAPI.Controls.GuideCamera?.RemoveConsumer(this);
        }

        public void StartWatchers()
        {
            IGuideCameraMediator cam = AdvancedAPI.Controls.GuideCamera;
            if (cam == null)
            {
                return;
            }
            cam.Connected += ConnectedHandler;
            cam.Disconnected += DisconnectedHandler;
            cam.DownloadTimeout += DownloadTimeoutHandler;
            cam.RegisterConsumer(this);
        }

        public void StopWatchers()
        {
            IGuideCameraMediator cam = AdvancedAPI.Controls.GuideCamera;
            if (cam == null)
            {
                return;
            }
            cam.Connected -= ConnectedHandler;
            cam.Disconnected -= DisconnectedHandler;
            cam.DownloadTimeout -= DownloadTimeoutHandler;
            cam.RemoveConsumer(this);
        }

        public async void UpdateDeviceInfo(CameraInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("GUIDECAMERA");
        }
    }

    public partial class ControllerV2
    {
        // Holds the guide camera's capture block while an API capture runs, so a guider using the camera sees it busy.
        private static readonly object guideCameraCaptureOwner = new();

        /// <summary>
        /// pins: takes one exposure with the guide camera and returns it, e.g. to focus or frame the guide camera.
        /// Unlike /equipment/camera/capture it doesn't go through the imaging pipeline: nothing is saved, added to
        /// the image history or plate solved, and the imaging camera's last capture stays as it is.
        /// </summary>
        [Route(HttpVerbs.Get, "/equipment/guidecamera/capture")]
        public async Task GuideCameraCapture(
            [QueryField] double duration,
            [QueryField] int gain,
            [QueryField] bool resize,
            [QueryField] string size,
            [QueryField] double scale,
            [QueryField] int quality,
            [QueryField] bool stream,
            [QueryField] bool skipAutoStretch)
        {
            HttpResponse response = new HttpResponse();
            IGuideCameraMediator cam = AdvancedAPI.Controls.GuideCamera;

            quality = Math.Clamp(quality, -1, 100);
            if (quality == 0)
                quality = -1; // png if omitted

            if (resize && string.IsNullOrWhiteSpace(size))
                size = "640x480";

            try
            {
                if (cam == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guide camera not available", 404));
                }
                else if (!cam.GetInfo().Connected)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guide camera not connected", 409));
                }
                // takes the capture block in one step: two requests (or a request and the guider) can't both start
                else if (cam.GetInfo().IsExposing || !cam.TryRegisterCaptureBlock(guideCameraCaptureOwner))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guide camera is busy", 409));
                }
                else
                {
                    IRenderedImage image;
                    try
                    {
                        image = await CaptureGuideFrame(cam, duration > 0 ? duration : 1, HttpContext.IsParameterOmitted(nameof(gain)) ? -1 : gain, skipAutoStretch);
                    }
                    finally
                    {
                        cam.ReleaseCaptureBlock(guideCameraCaptureOwner);
                    }

                    Size resolution = Size.Empty;
                    if (resize)
                    {
                        string[] s = size.Split('x');
                        resolution = new Size(int.Parse(s[0]), int.Parse(s[1]));
                    }

                    BitmapSource source = image.Image;
                    if (resize && scale == 0)
                        source = BitmapHelper.ResizeBitmap(source, resolution);
                    else if (resize)
                        source = BitmapHelper.ScaleBitmap(source, scale);

                    BitmapEncoder encoder = BitmapHelper.GetEncoder(source, quality);
                    if (stream)
                    {
                        HttpContext.Response.ContentType = quality == -1 ? "image/png" : "image/jpeg";
                        using MemoryStream memory = new MemoryStream();
                        encoder.Save(memory);
                        await HttpContext.Response.OutputStream.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length));
                        return;
                    }
                    response.Response = new CaptureResponse() { Image = BitmapHelper.EncoderToBase64(encoder) };
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        private static async Task<IRenderedImage> CaptureGuideFrame(ICameraMediator cam, double duration, int gain, bool skipAutoStretch)
        {
            CameraInfo info = cam.GetInfo();
            CaptureSequence sequence = new CaptureSequence(duration, CaptureSequence.ImageTypes.SNAPSHOT, null, new BinningMode(info.BinX, info.BinY), 1);
            if (gain >= 0)
            {
                sequence.Gain = gain;
            }

            // The exposure plus a generous download allowance.
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(duration + 120));
            IProgress<NINA.Core.Model.ApplicationStatus> progress = AdvancedAPI.Controls.StatusMediator.GetStatus();

            await cam.Capture(sequence, timeout.Token, progress);
            IExposureData exposure = await cam.Download(timeout.Token);
            IImageData imageData = await exposure.ToImageData(progress, timeout.Token);

            IProfile profile = AdvancedAPI.Controls.Profile.ActiveProfile;
            IRenderedImage image = imageData.RenderImage();

            SensorType sensor = DebayerPattern.SensorTypeFor(profile.GuideCameraSettings.BayerPattern, info);
            if (image.RawImageData.Properties.IsBayered && DebayerPattern.CanDebayer(sensor))
            {
                image = image.Debayer(saveColorChannels: true, saveLumChannel: true, bayerPattern: sensor);
            }

            if (!skipAutoStretch)
            {
                image = await image.Stretch(profile.ImageSettings.AutoStretchFactor, profile.ImageSettings.BlackClipping, profile.ImageSettings.UnlinkedStretch);
            }
            return image;
        }
    }
}
