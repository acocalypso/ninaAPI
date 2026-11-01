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
using EmbedIO.WebSockets;
using Newtonsoft.Json;
using NINA.Core.Utility;
using ninaAPI.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public partial class ControllerV2
    {
        [Route(HttpVerbs.Get, "/event-history")]
        public void GetEventHistory()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                List<object> result = new List<object>();
                foreach (HttpResponse r in WebSocketV2.GetEvents())
                {
                    result.Add(r.Response);
                }
                response.Response = result;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }
    }

    public class WebSocketV2 : WebSocketModule
    {
        private static bool sendConsumerEvents = false;

        private static WebSocketV2 instance;

        private readonly WebSocketClientQueues queues;

        public WebSocketV2(string urlPath) : base(urlPath, true)
        {
            queues = new WebSocketClientQueues("WebSocket", SendAsync, CloseAsync);
            instance = this;
        }

        public static async Task SendConsumerEvent(string consumer)
        {
            // Maybe not, because it would be a lot of unnecessary traffic since most devices update their info constantly and then this would be useless
            return;
            consumer = consumer.ToUpper();
            Logger.Info($"Sending {consumer}-INFO-UPDATED");
            if (sendConsumerEvents && consumer != "MOUNT")
            {
                // Do not allow mount updates for now, because that would be a lot of unnecessary traffic
                // because every time the coordinates change, the info also changes. We could enable this with an extra bool
                // in the future
                await SendEvent(new HttpResponse() { Response = $"{consumer}-INFO-UPDATED", Type = HttpResponse.TypeSocket });
            }
        }

        public static async Task SendAndAddEvent(string eventName, Dictionary<string, object> data)
        {
            await SendAndAddEvent(eventName, DateTime.Now, data);
        }

        public static async Task SendAndAddEvent(string eventName)
        {
            await SendAndAddEvent(eventName, DateTime.Now, null);
        }

        public static async Task SendAndAddEvent(string eventName, DateTime time)
        {
            await SendAndAddEvent(eventName, time, null);
        }

        /// <summary>
        /// Never throws: most callers are async void event handlers, and an exception from one of
        /// those ends the process when there is no synchronization context (headless pins).
        /// </summary>
        public static async Task SendAndAddEvent(string eventName, DateTime time, Dictionary<string, object> data)
        {
            try
            {
                HttpResponse response = new HttpResponse();
                response.Type = HttpResponse.TypeSocket;

                Hashtable responseData = new Hashtable
                {
                    { "Event", eventName }
                };
                if (data != null)
                {
                    foreach (KeyValuePair<string, object> kvp in data)
                    {
                        responseData.Add(kvp.Key, kvp.Value);
                    }
                }

                response.Response = responseData;

                // Copy the event as JSON, so later changes to the sent objects don't alter the history.
                // Read it back with System.Text.Json because /event-history is written with it: nested
                // objects read back by Newtonsoft would come out there as {"RA":[],...}.
                string json = JsonConvert.SerializeObject(responseData);
                Dictionary<string, object> eventTable = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                eventTable.Add("Time", time);
                HttpResponse Event = new HttpResponse() { Type = HttpResponse.TypeSocket, Response = eventTable };
                AddEvent(Event);

                await SendEvent(response);
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSocket event {eventName} failed", ex);
            }
        }

        public static bool IsAvailable => instance is not null;

        public static void SetUnavailable() => instance = null;

        // Every event since the API started; beyond this the oldest are dropped. Events arrive from
        // the threads of NINA's mediators and the log watcher while /event-history reads them.
        public const int MaxEvents = 10000;
        private static readonly object eventsLock = new object();
        private static readonly List<HttpResponse> events = new List<HttpResponse>();

        public static void AddEvent(HttpResponse e)
        {
            lock (eventsLock)
            {
                events.Add(e);
                if (events.Count > MaxEvents)
                {
                    events.RemoveRange(0, events.Count - MaxEvents);
                }
            }
        }

        public static List<HttpResponse> GetEvents()
        {
            lock (eventsLock)
            {
                return new List<HttpResponse>(events);
            }
        }

        protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] rxBuffer, IWebSocketReceiveResult rxResult)
        {
            string s = Encoding.GetString(rxBuffer);
            if (s.Equals("enable-consumer-events"))
            {
                sendConsumerEvents = true;
            }
            else if (s.Equals("disable-consumer-events"))
            {
                sendConsumerEvents = false;
            }
            return Task.CompletedTask;
        }

        protected override Task OnClientConnectedAsync(IWebSocketContext context)
        {
            Logger.Info("WebSocket connected " + context.RemoteEndPoint.ToString());
            queues.Add(context);
            return Task.CompletedTask;
        }

        protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
        {
            queues.Remove(context);
            return Task.CompletedTask;
        }

        public static async Task<bool> SendEvent(HttpResponse payload)
        {
            try
            {
                if (instance is not null)
                {
                    await instance?.Send(payload);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"WebSocket SendEvent failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Enqueues the payload for every client and returns at once: callers include handlers that
        /// NINA awaits, which must not wait for a slow or vanished client (see WebSocketClientQueues).
        /// </summary>
        public Task Send(HttpResponse payload)
        {
            queues.Broadcast(JsonConvert.SerializeObject(payload));
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                queues.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
