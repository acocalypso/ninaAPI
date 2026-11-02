#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using ninaAPI.WebService.V2;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.Utility
{
    /// <summary>
    /// The device operations the API starts in the background (park, filter change, focuser move, ...) answer the request
    /// at once. Their result was thrown away: a failed operation left no log line and no event, while the answer had
    /// already said "Parking" or "Filter changed". A failure (an exception, or false from an operation that reports its
    /// success) is now logged and sent as a WebSocket event with the error. A cancelled operation is no failure.
    /// </summary>
    public static class BackgroundOperation
    {
        public static void Report(Task operation, string failedEvent, string what)
        {
            _ = operation.ContinueWith(t => Failed(failedEvent, what, ErrorOf(t)),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        public static void Report(Task<bool> operation, string failedEvent, string what)
        {
            _ = operation.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Failed(failedEvent, what, ErrorOf(t));
                }
                else if (t.IsCompletedSuccessfully && !t.Result)
                {
                    Failed(failedEvent, what, "the operation did not succeed (see the NINA log)");
                }
            }, CancellationToken.None, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Default);
        }

        private static string ErrorOf(Task t) => t.Exception?.GetBaseException() is OperationCanceledException
            ? null
            : t.Exception?.GetBaseException().Message ?? "unknown error";

        private static void Failed(string failedEvent, string what, string error)
        {
            if (error is null)
            {
                // cancelled (a newer move, a stop), wrapped in the task's exception
                return;
            }

            Logger.Error($"{what} failed: {error}");
            _ = WebSocketV2.SendAndAddEvent(failedEvent, new Dictionary<string, object>() {
                { "Error", error }
            });
        }
    }
}
