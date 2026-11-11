using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;

namespace ninaAPI.WebService.V2;

// Add this source file to the existing ninaAPI project. No MLAstro DLL reference is required.
public partial class ControllerV2
{
    private static readonly HttpClient MlastroHttp = new() { Timeout = TimeSpan.FromSeconds(50) };

    [Route(HttpVerbs.Get, "/mlastro/status")]
    public Task MlastroStatus() => RelayMlastroAsync("status", false);
    [Route(HttpVerbs.Get, "/mlastro/settings")]
    public Task MlastroSettings() => RelayMlastroAsync("settings", false);
    [Route(HttpVerbs.Get, "/mlastro/watchdog")]
    public Task MlastroWatchdog() => RelayMlastroAsync("watchdog", false);
    [Route(HttpVerbs.Post, "/mlastro/watchdog")]
    public Task MlastroConfigureWatchdog() => RelayMlastroAsync("watchdog", true);
    [Route(HttpVerbs.Get, "/mlastro/diagnostics")]
    public Task MlastroDiagnostics() => RelayMlastroAsync("diagnostics", false);
    [Route(HttpVerbs.Post, "/mlastro/reconnect")]
    public Task MlastroReconnect() => RelayMlastroAsync("reconnect", true);
    [Route(HttpVerbs.Post, "/mlastro/connect")]
    public Task MlastroConnect() => RelayMlastroAsync("connect", true);
    [Route(HttpVerbs.Post, "/mlastro/disconnect")]
    public Task MlastroDisconnect() => RelayMlastroAsync("disconnect", true);
    [Route(HttpVerbs.Post, "/mlastro/automation")]
    public Task MlastroAutomation() => RelayMlastroAsync("automation", true);
    [Route(HttpVerbs.Post, "/mlastro/stop")]
    public Task MlastroStop() => RelayMlastroAsync("stop", true);
    [Route(HttpVerbs.Post, "/mlastro/move")]
    public Task MlastroMove() => RelayMlastroAsync("move", true);
    [Route(HttpVerbs.Post, "/mlastro/discover")]
    public Task MlastroDiscover() => RelayMlastroAsync("discover", true);
    [Route(HttpVerbs.Post, "/mlastro/select-controller")]
    public Task MlastroSelect() => RelayMlastroAsync("select-controller", true);
    [Route(HttpVerbs.Post, "/mlastro/settings")]
    public Task MlastroConfigure() => RelayMlastroAsync("settings", true);

    [Route(HttpVerbs.Get, "/mlastro/device-settings")]
    public Task MlastroDeviceSettings() => RelayMlastroAsync("device-settings", false);
    [Route(HttpVerbs.Post, "/mlastro/device-settings")]
    public Task MlastroConfigureDevice() => RelayMlastroAsync("device-settings", true);
    [Route(HttpVerbs.Post, "/mlastro/jog/start")]
    public Task MlastroJogStart() => RelayMlastroAsync("jog/start", true);
    [Route(HttpVerbs.Post, "/mlastro/jog/renew")]
    public Task MlastroJogRenew() => RelayMlastroAsync("jog/renew", true);
    [Route(HttpVerbs.Post, "/mlastro/jog/stop")]
    public Task MlastroJogStop() => RelayMlastroAsync("jog/stop", true);

    [Route(HttpVerbs.Get, "/mlastro/usb-devices")]
    public Task MlastroUsbDevices() => RelayMlastroAsync("usb-devices", false);

    private async Task RelayMlastroAsync(string operation, bool post)
    {
        try
        {
            using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, "http://127.0.0.1:1891/" + operation);
            if (post)
            {
                var body = await HttpContext.GetRequestBodyAsStringAsync();
                if (Encoding.UTF8.GetByteCount(body) > 4096) { HttpContext.Response.StatusCode = 413; return; }
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            using var response = await MlastroHttp.SendAsync(request);
            HttpContext.Response.StatusCode = (int)response.StatusCode;
            HttpContext.Response.ContentType = "application/json";
            await response.Content.CopyToAsync(HttpContext.Response.OutputStream);
        }
        catch (HttpRequestException) { await MlastroUnavailableAsync(); }
        catch (TaskCanceledException) { await MlastroUnavailableAsync(); }
    }
    private async Task MlastroUnavailableAsync()
    {
        HttpContext.Response.StatusCode = 503;
        HttpContext.Response.ContentType = "application/json";
        await HttpContext.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":\"MLAstro plugin unavailable or timed out\"}"));
    }
}
