﻿// -----------------------------------------------------------------------
// <copyright file="ApplicationInsightsTarget.cs" company="Microsoft">
// Copyright (c) Microsoft Corporation. 
// All rights reserved.  2013
// </copyright>
// -----------------------------------------------------------------------

namespace Microsoft.ApplicationInsights.NLogTarget
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;
    using Microsoft.ApplicationInsights.Implementation;

    using NLog;
    using NLog.Common;
    using NLog.Targets;

    /// <summary>
    /// NLog Target that routes all logging output to the Application Insights logging framework.
    /// The messages will be uploaded to the Application Insights cloud service.
    /// </summary>
    [Target("ApplicationInsightsTarget")]   
    public sealed class ApplicationInsightsTarget : TargetWithLayout
    {
        private TelemetryClient telemetryClient;
        private DateTime lastLogEventTime;

        /// <summary>
        /// Initializers a new instance of ApplicationInsightsTarget type.
        /// </summary>
        public ApplicationInsightsTarget()
        {
            this.Layout = @"${message}";
        }

        /// <summary>
        /// Gets or sets the Application Insights instrumentationKey for your application. 
        /// </summary>
        public string InstrumentationKey { get; set; }

        /// <summary>
        /// Gets the logging controller we will be using.
        /// </summary>
        internal TelemetryClient TelemetryClient
        {
            get { return this.telemetryClient; }
        }

        internal void BuildPropertyBag(LogEventInfo logEvent, ITelemetry trace)
        {
            trace.Timestamp = logEvent.TimeStamp;
            trace.Sequence = logEvent.SequenceID.ToString(CultureInfo.InvariantCulture);

            IDictionary<string, string> propertyBag;

            if (trace is ExceptionTelemetry)
            {
                propertyBag = ((ExceptionTelemetry)trace).Properties;
            }
            else
            {
                propertyBag = ((TraceTelemetry)trace).Properties;
            }

            if (!string.IsNullOrEmpty(logEvent.LoggerName))
            {
                propertyBag.Add("LoggerName", logEvent.LoggerName);
            }

            if (logEvent.UserStackFrame != null)
            {
                propertyBag.Add("UserStackFrame", logEvent.UserStackFrame.ToString());
                propertyBag.Add("UserStackFrameNumber", logEvent.UserStackFrameNumber.ToString(CultureInfo.InvariantCulture));
            }

            this.LoadGlobalDiagnosticsContextProperties(propertyBag);
            this.LoadLogEventProperties(logEvent, propertyBag);
        }

        /// <summary>
        /// Initializes the Target and perform instrumentationKey validation.
        /// </summary>
        /// <exception cref="NLogConfigurationException">Will throw when <see cref="InstrumentationKey"/> is not set.</exception>
        protected override void InitializeTarget()
        {
            base.InitializeTarget();
            this.telemetryClient = new TelemetryClient();
            if (!string.IsNullOrEmpty(this.InstrumentationKey))
            {
                this.telemetryClient.Context.InstrumentationKey = this.InstrumentationKey;
            }

            this.telemetryClient.Context.GetInternalContext().SdkVersion = SdkVersionUtils.GetSdkVersion("nlog:");
        }

        /// <summary>
        /// Send the log message to Application Insights.
        /// </summary>
        /// <exception cref="ArgumentNullException">If <paramref name="logEvent"/> is null.</exception>
        protected override void Write(LogEventInfo logEvent)
        {
            if (logEvent == null)
            {
                throw new ArgumentNullException("logEvent");
            }

            this.lastLogEventTime = DateTime.UtcNow;

            if (logEvent.Exception != null)
            {
                this.SendException(logEvent);
            }
            else
            {
                this.SendTrace(logEvent);
            }
        }

        /// <summary>
        /// Flush any pending log messages
        /// </summary>
        /// <param name="asyncContinuation">The asynchronous continuation</param>
        protected override void FlushAsync(AsyncContinuation asyncContinuation)
        {
            try
            {
                this.TelemetryClient.Flush();
                if (DateTime.UtcNow.AddSeconds(-30) > this.lastLogEventTime)
                {
                    // Nothing has been written, so nothing to wait for
                    asyncContinuation(null);
                }
                else
                {
                    // Documentation says it is important to wait after flush, else nothing will happen
                    // https://docs.microsoft.com/azure/application-insights/app-insights-api-custom-events-metrics#flushing-data
                    System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(500)).ContinueWith((task) => asyncContinuation(null));
                }
            }
            catch (Exception ex)
            {
                asyncContinuation(ex);
            }
        }

        private void SendException(LogEventInfo logEvent)
        {
            var exceptionTelemetry = new ExceptionTelemetry(logEvent.Exception)
            {
                SeverityLevel = this.GetSeverityLevel(logEvent.Level)
            };

            string logMessage = this.Layout.Render(logEvent);
            exceptionTelemetry.Properties.Add("Message", logMessage);

            this.BuildPropertyBag(logEvent, exceptionTelemetry);
            this.telemetryClient.Track(exceptionTelemetry);
        }

        private void SendTrace(LogEventInfo logEvent)
        {
            string logMessage = this.Layout.Render(logEvent);
            
            var trace = new TraceTelemetry(logMessage)
            {
                SeverityLevel = this.GetSeverityLevel(logEvent.Level)
            };

            this.BuildPropertyBag(logEvent, trace);
            this.telemetryClient.Track(trace);
        }

        private void LoadGlobalDiagnosticsContextProperties(IDictionary<string, string> propertyBag)
        {
            foreach (string key in GlobalDiagnosticsContext.GetNames())
            {
                this.PopulatePropertyBag(propertyBag, key, GlobalDiagnosticsContext.GetObject(key));
            }
        }

        private void LoadLogEventProperties(LogEventInfo logEvent, IDictionary<string, string> propertyBag)
        {
            var properties = logEvent.Properties;
            if (properties != null)
            {
                foreach (var keyValuePair in properties)
                {
                    string key = keyValuePair.Key.ToString();
                    object valueObj = keyValuePair.Value;
                    this.PopulatePropertyBag(propertyBag, key, valueObj);
                }
            }
        }

        private void PopulatePropertyBag(IDictionary<string, string> propertyBag, string key, object valueObj)
        {
            if (valueObj == null)
            {
                return;
            }

            string value = valueObj.ToString();
            if (propertyBag.ContainsKey(key))
            {
                if (value == propertyBag[key])
                {
                    return;
                }

                key += "_1";
            }

            propertyBag.Add(key, value);
        }

        private SeverityLevel? GetSeverityLevel(LogLevel logEventLevel)
        {
            if (logEventLevel == null)
            {
                return null;
            }

            if (logEventLevel.Ordinal == LogLevel.Trace.Ordinal ||
                logEventLevel.Ordinal == LogLevel.Debug.Ordinal)
            {
                return SeverityLevel.Verbose;
            }

            if (logEventLevel.Ordinal == LogLevel.Info.Ordinal)
            {
                return SeverityLevel.Information;
            }

            if (logEventLevel.Ordinal == LogLevel.Warn.Ordinal)
            {
                return SeverityLevel.Warning;
            }

            if (logEventLevel.Ordinal == LogLevel.Error.Ordinal)
            {
                return SeverityLevel.Error;
            }

            if (logEventLevel.Ordinal == LogLevel.Fatal.Ordinal)
            {
                return SeverityLevel.Critical;
            }

            // The only possible value left if OFF but we should never get here in this case
            return null;
        }
    }
}