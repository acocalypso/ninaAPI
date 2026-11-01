#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using EmbedIO.WebSockets;
using NINA.Core.Utility;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ninaAPI.Utility
{
    /// <summary>
    /// Sends a WebSocket module's messages to its clients without making the caller wait for them.
    ///
    /// Events reach the sockets from handlers that NINA awaits (meridian flip, park, dither, filter
    /// change, connect). Sending to the clients one after another there let a client that left the
    /// network without closing its connection hold up NINA until the TCP timeout, minutes later.
    /// Here every client has its own bounded queue and one sender task, so <see cref="Broadcast"/>
    /// only enqueues, a slow client loses its own oldest messages, and only one send runs per socket
    /// at a time (EmbedIO does not serialize concurrent sends on a socket). A send that takes longer
    /// than <see cref="SendTimeout"/> drops the client.
    /// </summary>
    public sealed class WebSocketClientQueues : IDisposable
    {
        public const int QueueSize = 256;

        public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);

        private sealed class Client
        {
            public Client(IWebSocketContext context, Channel<string> queue)
            {
                Context = context;
                Queue = queue;
            }

            public IWebSocketContext Context { get; }

            public Channel<string> Queue { get; }

            public int Dropped;
        }

        private readonly string name;
        private readonly Func<IWebSocketContext, string, Task> send;
        private readonly Func<IWebSocketContext, Task> close;
        private readonly ConcurrentDictionary<string, Client> clients = new();
        private readonly CancellationTokenSource lifetime = new();

        /// <param name="name">The socket's name for the log.</param>
        /// <param name="send">The module's <c>SendAsync</c>.</param>
        /// <param name="close">The module's <c>CloseAsync</c>, used for a client whose send timed out.</param>
        public WebSocketClientQueues(string name, Func<IWebSocketContext, string, Task> send, Func<IWebSocketContext, Task> close)
        {
            this.name = name;
            this.send = send;
            this.close = close;
        }

        public void Add(IWebSocketContext context)
        {
            Client client = null;
            var queue = Channel.CreateBounded<string>(
                new BoundedChannelOptions(QueueSize)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                },
                _ => OnDropped(client));
            client = new Client(context, queue);

            if (clients.TryAdd(context.Id, client))
            {
                _ = Task.Run(() => SendLoopAsync(client));
            }
        }

        public void Remove(IWebSocketContext context)
        {
            if (clients.TryRemove(context.Id, out Client client))
            {
                client.Queue.Writer.TryComplete();
            }
        }

        /// <summary>Enqueues the message for every client and returns at once.</summary>
        public void Broadcast(string json)
        {
            foreach (Client client in clients.Values)
            {
                client.Queue.Writer.TryWrite(json);
            }
        }

        private void OnDropped(Client client)
        {
            // Once per client, so a stalled client doesn't flood the log
            if (client != null && Interlocked.Increment(ref client.Dropped) == 1)
            {
                Logger.Warning($"{name}: client {client.Context.RemoteEndPoint} doesn't keep up, its oldest messages are dropped");
            }
        }

        private async Task SendLoopAsync(Client client)
        {
            try
            {
                while (await client.Queue.Reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
                {
                    while (client.Queue.Reader.TryRead(out string json))
                    {
                        Task sending = send(client.Context, json);
                        try
                        {
                            await sending.WaitAsync(SendTimeout, lifetime.Token).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            Logger.Warning($"{name}: sending to client {client.Context.RemoteEndPoint} took longer than {SendTimeout.TotalSeconds:0} s, dropping the client");
                            Observe(sending);
                            Drop(client);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Debug($"{name}: send loop of client {client.Context.RemoteEndPoint} ended: {ex.Message}");
                clients.TryRemove(client.Context.Id, out _);
                client.Queue.Writer.TryComplete();
            }
        }

        private void Drop(Client client)
        {
            clients.TryRemove(client.Context.Id, out _);
            client.Queue.Writer.TryComplete();

            // Not awaited: the close frame queues behind the stuck send
            Task closing;
            try
            {
                closing = close(client.Context);
            }
            catch (Exception ex)
            {
                Logger.Debug($"{name}: closing client {client.Context.RemoteEndPoint} failed: {ex.Message}");
                return;
            }
            Observe(closing);
        }

        // The abandoned send or close still fails once the connection is gone; observe it so it
        // doesn't end up as an unobserved task exception.
        private static void Observe(Task task)
        {
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        public void Dispose()
        {
            lifetime.Cancel();
            foreach (Client client in clients.Values)
            {
                client.Queue.Writer.TryComplete();
            }
            clients.Clear();
        }
    }
}
