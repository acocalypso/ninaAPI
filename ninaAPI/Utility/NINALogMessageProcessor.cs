using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;


// https://github.com/tcpalmer/nina.plugin.web/blob/main/NINA.Plugin.Web/NINA.Plugin.Web/LogEvent/NINALogMessageProcessor.cs (modified)
namespace ninaAPI
{
    public class NINALogMessageProcessor
    {
        private Dictionary<Regex, EventMatcher> matchers;

        // Pairing of the plate solve lines, see PlateSolveStarted
        private bool solveOpen;
        private string openSolveFailoverParameters;
        private bool resultEchoPending;

        public NINALogMessageProcessor()
        {
            matchers = initMatchers();
        }

        public void processLogMessages(List<string> messages)
        {
            Logger.Trace($"processing {messages.Count} new log messages");

            foreach (var line in messages)
            {
                // the watcher thread ends at the first exception, and no log event was sent after it for the rest of the session
                try
                {
                    processLogMessage(line);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"log message could not be processed: {ex.Message}");
                }
            }
        }

        private void processLogMessage(string line)
        {
            if (filterMessage(line))
            {
                return;
            }

            // the message itself may contain '|' (e.g. a firmware "model|version"); such lines were dropped
            string[] parts = line.Split('|', 6);
            if (parts.Length == 6 && DateTime.TryParse(parts[0], out DateTime dateTime))
            {
                string message = parts[5];

                foreach (Regex regex in matchers.Keys)
                {
                    Match match = regex.Match(message);
                    if (match.Success)
                    {
                        EventMatcher eventMatcher = matchers[regex];
                        NINALogEvent logEvent = eventMatcher.handleMessage(eventMatcher, message, dateTime, match);
                        if (logEvent != null)
                        {
                            onNINALogEvent(logEvent);
                        }
                    }
                }
            }
            else
            {
                Logger.Trace($"log message is not regular form: {line}, skipping");
            }
        }

        public event EventHandler<NINALogEvent> NINALogEventSaved;

        public void onNINALogEvent(NINALogEvent e)
        {
            Logger.Debug($"detected event for web viewer: {e.type}");
            NINALogEventSaved?.Invoke(this, e);
        }

        private Dictionary<Regex, EventMatcher> initMatchers()
        {
            Dictionary<Regex, EventMatcher> _matchers = new Dictionary<Regex, EventMatcher>();

            RegexOptions options = RegexOptions.Compiled | RegexOptions.IgnoreCase;

            // Dome stopped
            Regex re = new Regex("^Stopping all dome movement$", options);
            _matchers.Add(re, new EventMatcher(NINALogEvent.NINA_DOME_STOPPED, false, null));

            // Center / Plate solve in Sequence
            re = new Regex("^Starting Category: Telescope, Item: Center, (?<extra>.+)$", options);
            _matchers.Add(re, new EventMatcher(NINALogEvent.NINA_CENTER, true, null));

            // Error: Auto Focus
            re = new Regex("^Autofocus failed to run(?<extra>.+)$", options);
            _matchers.Add(re, new EventMatcher(NINALogEvent.NINA_ERROR_AF, true, null));

            // Error: Plate solve
            re = new Regex("^ASTAP - Plate solve failed.", options);
            _matchers.Add(re, new EventMatcher(NINALogEvent.NINA_ERROR_PLATESOLVE, false, null));

            // Plate solves, as logged by NINA's ImageSolver for every solver
            re = new Regex("^Platesolving with parameters:", options);
            _matchers.Add(re, new EventMatcher(NINALogEvent.NINA_PLATESOLVE_START, false, PlateSolveStarted));

            re = new Regex("^Platesolve successful:", options);
            _matchers.Add(re, new EventMatcher(NINALogEvent.NINA_PLATESOLVE_SUCCESS, false, PlateSolveFinished));

            re = new Regex("^Platesolve failed$", options);
            _matchers.Add(re, new EventMatcher(NINALogEvent.NINA_PLATESOLVE_FAILED, false, PlateSolveFinished));

            return _matchers;
        }

        // With blind failover, ImageSolver.Solve calls itself when the solve with coordinates fails. That
        // failure is only logged at Debug level; the blind solve logs its start and result, and the outer
        // call then logs the same result again. Report the failure when the blind solve starts and drop
        // the repeated result, so that every PLATESOLVE-START is followed by exactly one result.
        //
        // Best effort: the log lines carry no id of the solve. Solves without an exposure (a prepared or
        // saved image, a FITS file) can run while a sequence centers; a blind start is taken for the
        // failover only when it logs the open solve's parameters without its coordinates, but one with the
        // same parameters is still taken for it. A solve with coordinates started in between (the app's
        // solve passes the mount position) hides the failover, and its repeated result reaches the clients.
        private NINALogEvent PlateSolveStarted(EventMatcher eventMatcher, string msg, DateTime dateTime, Match match)
        {
            int coordinatesAt = msg.IndexOf("Reference Coordinates", StringComparison.Ordinal);
            bool hasCoordinates = coordinatesAt >= 0;
            resultEchoPending = solveOpen
                && openSolveFailoverParameters != null
                && !hasCoordinates
                && msg.TrimEnd() == openSolveFailoverParameters;
            if (resultEchoPending)
            {
                onNINALogEvent(new NINALogEvent(NINALogEvent.NINA_PLATESOLVE_FAILED, dateTime));
            }
            solveOpen = true;
            openSolveFailoverParameters = hasCoordinates && msg.Contains("BlindFailoverEnabled: True")
                ? msg[..coordinatesAt].TrimEnd()
                : null;
            return new NINALogEvent(eventMatcher.eventType, dateTime);
        }

        private NINALogEvent PlateSolveFinished(EventMatcher eventMatcher, string msg, DateTime dateTime, Match match)
        {
            if (!solveOpen && resultEchoPending)
            {
                resultEchoPending = false;
                return null;
            }
            solveOpen = false;
            return new NINALogEvent(eventMatcher.eventType, dateTime);
        }

        private bool filterMessage(string line)
        {
            if (line?.Length == 0)
            {
                return true;
            }

            if (line.Contains("|DEBUG|") || line.Contains("|TRACE|"))
            {
                return true;
            }

            return false;
        }

        public delegate NINALogEvent HandleMessageDelegate(EventMatcher eventMatcher, string msg, DateTime dateTime, Match match);

        public class EventMatcher
        {
            public string eventType { get; }
            public bool hasExtra { get; }
            public HandleMessageDelegate customMessageHandler;

            public EventMatcher(string eventType, bool hasExtra, HandleMessageDelegate handleMessage)
            {
                this.eventType = eventType;
                this.hasExtra = hasExtra;
                this.customMessageHandler = handleMessage;
            }

            public NINALogEvent handleMessage(EventMatcher eventMatcher, string msg, DateTime dateTime, Match match)
            {
                if (customMessageHandler != null)
                {
                    return customMessageHandler(eventMatcher, msg, dateTime, match);
                }

                if (eventMatcher.hasExtra && match.Groups.Count > 0)
                {
                    return new NINALogEvent(eventMatcher.eventType, dateTime, match.Groups["extra"].Value);
                }
                else
                {
                    return new NINALogEvent(eventMatcher.eventType, dateTime);
                }
            }
        }
    }
}