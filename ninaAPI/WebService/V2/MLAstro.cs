#region "copyright"

/*
    Copyright © 2026 MLAstro RPA for PINS contributors (https://github.com/ydutertre/pins-mlastro-rpa)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using EmbedIO;
using EmbedIO.Routing;
using NINA.Core.Utility;
using ninaAPI.Utility;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    /// <summary>
    /// Transparent relay to the control service of the MLAstro RPA plugin, which listens on
    /// loopback only and refuses browser requests. The plugin's answer is passed through as it
    /// is: its HTTP status, content type and JSON body. These routes therefore do not use the
    /// response envelope of the other routes - a hardware client has to see the real status
    /// (409 state conflict, 400 invalid input, 504 plugin deadline).
    ///
    /// Errors raised by the relay itself are JSON objects { "error": string, "available": bool }:
    ///   413  request body larger than <see cref="MlastroMaxBodyBytes"/>
    ///   503  the plugin is not installed or not running; the command was not delivered
    ///   502  the connection broke after the request was sent; the command may have run
    ///   504  the plugin did not answer in time; the command may have run
    ///   500  unexpected relay failure, see the log
    ///
    /// The route list is fixed on purpose: only these operations reach the loopback service.
    /// Query strings are not forwarded, the plugin reads none.
    /// </summary>
    public partial class ControllerV2
    {
        private const int MlastroMaxBodyBytes = 4096;

        // Reads are polled and answer from memory. Writes stay above the plugin's own deadline
        // (45 s for connect/reconnect) so that the plugin, not the relay, reports a timeout.
        private static readonly TimeSpan MlastroReadTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MlastroWriteTimeout = TimeSpan.FromSeconds(50);
        private static readonly TimeSpan MlastroLogInterval = TimeSpan.FromSeconds(60);

        private static readonly HttpClient MlastroHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly ConcurrentDictionary<string, long> MlastroLastLogged = new();

        [Route(HttpVerbs.Get, "/mlastro/status")]
        public Task MlastroStatus() => RelayMlastroAsync("status", false);

        [Route(HttpVerbs.Get, "/mlastro/usb-devices")]
        public Task MlastroUsbDevices() => RelayMlastroAsync("usb-devices", false);

        [Route(HttpVerbs.Get, "/mlastro/diagnostics")]
        public Task MlastroDiagnostics() => RelayMlastroAsync("diagnostics", false);

        [Route(HttpVerbs.Post, "/mlastro/connect")]
        public Task MlastroConnect() => RelayMlastroAsync("connect", true);

        [Route(HttpVerbs.Post, "/mlastro/disconnect")]
        public Task MlastroDisconnect() => RelayMlastroAsync("disconnect", true);

        [Route(HttpVerbs.Post, "/mlastro/reconnect")]
        public Task MlastroReconnect() => RelayMlastroAsync("reconnect", true);

        [Route(HttpVerbs.Post, "/mlastro/move")]
        public Task MlastroMove() => RelayMlastroAsync("move", true);

        [Route(HttpVerbs.Post, "/mlastro/stop")]
        public Task MlastroStop() => RelayMlastroAsync("stop", true);

        [Route(HttpVerbs.Get, "/mlastro/settings")]
        public Task MlastroGetSettings() => RelayMlastroAsync("settings", false);

        [Route(HttpVerbs.Post, "/mlastro/settings")]
        public Task MlastroSetSettings() => RelayMlastroAsync("settings", true);

        [Route(HttpVerbs.Get, "/mlastro/device-settings")]
        public Task MlastroGetDeviceSettings() => RelayMlastroAsync("device-settings", false);

        [Route(HttpVerbs.Post, "/mlastro/device-settings")]
        public Task MlastroSetDeviceSettings() => RelayMlastroAsync("device-settings", true);

        [Route(HttpVerbs.Get, "/mlastro/watchdog")]
        public Task MlastroGetWatchdog() => RelayMlastroAsync("watchdog", false);

        [Route(HttpVerbs.Post, "/mlastro/watchdog")]
        public Task MlastroSetWatchdog() => RelayMlastroAsync("watchdog", true);

        [Route(HttpVerbs.Post, "/mlastro/discover")]
        public Task MlastroDiscover() => RelayMlastroAsync("discover", true);

        [Route(HttpVerbs.Post, "/mlastro/select-controller")]
        public Task MlastroSelectController() => RelayMlastroAsync("select-controller", true);

        [Route(HttpVerbs.Post, "/mlastro/automation")]
        public Task MlastroAutomation() => RelayMlastroAsync("automation", true);

        private async Task RelayMlastroAsync(string operation, bool post)
        {
            var elapsed = Stopwatch.StartNew();

            // A client that goes away must not cancel a command that is already on its way to
            // the hardware, so only reads follow the client's connection.
            using var deadline = post
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(HttpContext.CancellationToken);
            deadline.CancelAfter(post ? MlastroWriteTimeout : MlastroReadTimeout);

            try
            {
                using var request = new HttpRequestMessage(
                    post ? HttpMethod.Post : HttpMethod.Get,
                    $"http://127.0.0.1:{CoreUtility.MlastroControlPort}/{operation}");

                if (post)
                {
                    if (HttpContext.Request.ContentLength64 > MlastroMaxBodyBytes)
                    {
                        await WriteMlastroErrorAsync(413, $"The request body must not exceed {MlastroMaxBodyBytes} bytes.", true);
                        return;
                    }

                    // Checked again after reading: a chunked request announces no length.
                    var body = await HttpContext.GetRequestBodyAsStringAsync();
                    if (Encoding.UTF8.GetByteCount(body) > MlastroMaxBodyBytes)
                    {
                        await WriteMlastroErrorAsync(413, $"The request body must not exceed {MlastroMaxBodyBytes} bytes.", true);
                        return;
                    }

                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                using var response = await MlastroHttp.SendAsync(request, deadline.Token);

                HttpContext.Response.StatusCode = (int)response.StatusCode;
                HttpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? MimeType.Json;
                await response.Content.CopyToAsync(HttpContext.Response.OutputStream);
            }
            catch (OperationCanceledException) when (!post && HttpContext.CancellationToken.IsCancellationRequested)
            {
                // The client is gone; there is nobody to answer.
            }
            catch (OperationCanceledException ex)
            {
                LogMlastroFailure(operation, post, ex, elapsed.Elapsed);
                await WriteMlastroErrorAsync(504, post
                    ? "The MLAstro RPA plugin did not answer in time. The command may have been executed: check the status before repeating it."
                    : "The MLAstro RPA plugin did not answer in time.", true);
            }
            catch (HttpRequestException ex) when (IsMlastroConnectionRefused(ex))
            {
                LogMlastroFailure(operation, post, ex, elapsed.Elapsed);
                await WriteMlastroErrorAsync(503, "The MLAstro RPA plugin is not installed or not running. The request was not delivered.", false);
            }
            catch (HttpRequestException ex)
            {
                LogMlastroFailure(operation, post, ex, elapsed.Elapsed);
                await WriteMlastroErrorAsync(502, post
                    ? "The connection to the MLAstro RPA plugin was lost. The command may have been executed: check the status before repeating it."
                    : "The connection to the MLAstro RPA plugin was lost.", true);
            }
            catch (Exception ex)
            {
                Logger.Error($"MLAstro relay {(post ? "POST" : "GET")} {operation} failed after {elapsed.ElapsedMilliseconds} ms", ex);
                await WriteMlastroErrorAsync(500, "The MLAstro relay failed; see the log.", true);
            }
        }

        private static bool IsMlastroConnectionRefused(HttpRequestException exception)
        {
            for (Exception inner = exception; inner != null; inner = inner.InnerException)
            {
                if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                {
                    return true;
                }
            }

            return false;
        }

        // Clients poll the status. Without a limit a missing plugin would fill the log, so
        // each kind of failure per operation is written at most once a minute.
        private static void LogMlastroFailure(string operation, bool post, Exception exception, TimeSpan elapsed)
        {
            var key = $"{(post ? "POST" : "GET")} {operation} {exception.GetType().Name}";
            var now = Environment.TickCount64;
            var last = MlastroLastLogged.GetOrAdd(key, long.MinValue);
            if (last != long.MinValue && now - last < MlastroLogInterval.TotalMilliseconds)
            {
                return;
            }

            MlastroLastLogged[key] = now;
            Logger.Warning($"MLAstro relay {key} after {(long)elapsed.TotalMilliseconds} ms: {exception.Message}");
        }

        private async Task WriteMlastroErrorAsync(int statusCode, string message, bool available)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { error = message, available });
                HttpContext.Response.StatusCode = statusCode;
                HttpContext.Response.ContentType = MimeType.Json;
                HttpContext.Response.ContentLength64 = bytes.Length;
                await HttpContext.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex)
            {
                // The answer was already started or the client is gone.
                Logger.Debug($"MLAstro relay could not write its {statusCode} answer: {ex.Message}");
            }
        }
    }
}
