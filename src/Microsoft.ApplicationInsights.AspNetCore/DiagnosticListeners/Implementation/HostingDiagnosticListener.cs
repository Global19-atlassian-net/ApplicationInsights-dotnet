﻿namespace Microsoft.ApplicationInsights.AspNetCore.DiagnosticListeners
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Microsoft.ApplicationInsights.AspNetCore.DiagnosticListeners.Implementation;
    using Microsoft.ApplicationInsights.AspNetCore.Extensibility.Implementation.Tracing;
    using Microsoft.ApplicationInsights.AspNetCore.Extensions;
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.Common;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;
    using Microsoft.ApplicationInsights.Extensibility.Implementation.Experimental;
    using Microsoft.ApplicationInsights.Extensibility.W3C;
    using Microsoft.ApplicationInsights.W3C;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Primitives;

    /// <summary>
    /// <see cref="IApplicationInsightDiagnosticListener"/> implementation that listens for events specific to AspNetCore hosting layer.
    /// </summary>
    internal class HostingDiagnosticListener : IApplicationInsightDiagnosticListener
    {
        private const string ActivityCreatedByHostingDiagnosticListener = "ActivityCreatedByHostingDiagnosticListener";
        private const string ProactiveSamplingFeatureFlagName = "proactiveSampling";
        private const string ConditionalAppIdFeatureFlagName = "conditionalAppId";
        private static readonly Regex TraceIdRegex = new Regex("^[a-f0-9]{32}$", RegexOptions.Compiled);

        /// <summary>
        /// Determine whether the running AspNetCore Hosting version is 2.0 or higher. This will affect what DiagnosticSource events we receive.
        /// To support AspNetCore 1.0 and 2.0, we listen to both old and new events.
        /// If the running AspNetCore version is 2.0, both old and new events will be sent. In this case, we will ignore the old events.
        /// </summary>
        private readonly bool enableNewDiagnosticEvents;

        private readonly bool proactiveSamplingEnabled = false;
        private readonly bool conditionalAppIdEnabled = false;

        private readonly TelemetryConfiguration configuration;
        private readonly TelemetryClient client;
        private readonly IApplicationIdProvider applicationIdProvider;
        private readonly string sdkVersion = SdkVersionUtils.GetVersion();
        private readonly bool injectResponseHeaders;
        private readonly bool trackExceptions;
        private readonly bool enableW3CHeaders;
        private static readonly ActiveSubsciptionManager SubscriptionManager = new ActiveSubsciptionManager();

        #region fetchers

        // fetch is unique per event and per property
        private readonly PropertyFetcher httpContextFetcherOnBeforeAction = new PropertyFetcher("httpContext");
        private readonly PropertyFetcher routeDataFetcher = new PropertyFetcher("routeData");
        private readonly PropertyFetcher routeValuesFetcher = new PropertyFetcher("Values");
        private readonly PropertyFetcher httpContextFetcherStart = new PropertyFetcher("HttpContext");
        private readonly PropertyFetcher httpContextFetcherStop = new PropertyFetcher("HttpContext");
        private readonly PropertyFetcher httpContextFetcherBeginRequest = new PropertyFetcher("httpContext");
        private readonly PropertyFetcher httpContextFetcherEndRequest = new PropertyFetcher("httpContext");
        private readonly PropertyFetcher httpContextFetcherDiagExceptionUnhandled = new PropertyFetcher("httpContext");
        private readonly PropertyFetcher httpContextFetcherDiagExceptionHandled = new PropertyFetcher("httpContext");
        private readonly PropertyFetcher httpContextFetcherHostingExceptionUnhandled = new PropertyFetcher("httpContext");
        private readonly PropertyFetcher exceptionFetcherDiagExceptionUnhandled = new PropertyFetcher("exception");
        private readonly PropertyFetcher exceptionFetcherDiagExceptionHandled = new PropertyFetcher("exception");
        private readonly PropertyFetcher exceptionFetcherHostingExceptionUnhandled = new PropertyFetcher("exception");

        private readonly PropertyFetcher timestampFetcherBeginRequest = new PropertyFetcher("timestamp");
        private readonly PropertyFetcher timestampFetcherEndRequest = new PropertyFetcher("timestamp");
        #endregion

        private string lastIKeyLookedUp;
        private string lastAppIdUsed;

        internal const string LegacyRootIdProperty = "ai_legacyRootId";

        /// <summary>
        /// Initializes a new instance of the <see cref="HostingDiagnosticListener"/> class.
        /// </summary>
        /// <param name="client"><see cref="TelemetryClient"/> to post traces to.</param>
        /// <param name="applicationIdProvider">Provider for resolving application Id to be used in multiple instruemntation keys scenarios.</param>
        /// <param name="injectResponseHeaders">Flag that indicates that response headers should be injected.</param>
        /// <param name="trackExceptions">Flag that indicates that exceptions should be tracked.</param>
        /// <param name="enableW3CHeaders">Flag that indicates that W3C header parsing should be enabled.</param>
        /// <param name="enableNewDiagnosticEvents">Flag that indicates that new diagnostic events are supported by AspNetCore</param>
        public HostingDiagnosticListener(
            TelemetryClient client,
            IApplicationIdProvider applicationIdProvider,
            bool injectResponseHeaders,
            bool trackExceptions,
            bool enableW3CHeaders,
            bool enableNewDiagnosticEvents = true)
        {
            this.enableNewDiagnosticEvents = enableNewDiagnosticEvents;
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.applicationIdProvider = applicationIdProvider;
            this.injectResponseHeaders = injectResponseHeaders;
            this.trackExceptions = trackExceptions;
            this.enableW3CHeaders = enableW3CHeaders;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HostingDiagnosticListener"/> class.
        /// </summary>
        /// <param name="configuration"><see cref="TelemetryConfiguration"/> as a settings source.</param>
        /// <param name="client"><see cref="TelemetryClient"/> to post traces to.</param>
        /// <param name="applicationIdProvider">Provider for resolving application Id to be used in multiple instruemntation keys scenarios.</param>
        /// <param name="injectResponseHeaders">Flag that indicates that response headers should be injected.</param>
        /// <param name="trackExceptions">Flag that indicates that exceptions should be tracked.</param>
        /// <param name="enableW3CHeaders">Flag that indicates that W3C header parsing should be enabled.</param>
        /// <param name="enableNewDiagnosticEvents">Flag that indicates that new diagnostic events are supported by AspNetCore</param>
        public HostingDiagnosticListener(
            TelemetryConfiguration configuration,
            TelemetryClient client,
            IApplicationIdProvider applicationIdProvider,
            bool injectResponseHeaders,
            bool trackExceptions,
            bool enableW3CHeaders,
            bool enableNewDiagnosticEvents = true)
            : this(client, applicationIdProvider, injectResponseHeaders, trackExceptions, enableW3CHeaders, enableNewDiagnosticEvents)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.proactiveSamplingEnabled = this.configuration.EvaluateExperimentalFeature(ProactiveSamplingFeatureFlagName);
            this.conditionalAppIdEnabled = this.configuration.EvaluateExperimentalFeature(ConditionalAppIdFeatureFlagName);
        }

        /// <inheritdoc />
        public void OnSubscribe()
        {
            SubscriptionManager.Attach(this);
        }

        /// <inheritdoc/>
        public string ListenerName { get; } = "Microsoft.AspNetCore";

        /// <summary>
        /// Diagnostic event handler method for 'Microsoft.AspNetCore.Mvc.BeforeAction' event
        /// </summary>
        public void OnBeforeAction(HttpContext httpContext, IDictionary<string, object> routeValues)
        {
            var telemetry = httpContext.Features.Get<RequestTelemetry>();

            if (telemetry != null && string.IsNullOrEmpty(telemetry.Name))
            {
                string name = this.GetNameFromRouteContext(routeValues);

                if (!string.IsNullOrEmpty(name))
                {
                    name = httpContext.Request.Method + " " + name;
                    telemetry.Name = name;
                }
            }
        }

        private string GetNameFromRouteContext(IDictionary<string, object> routeValues)
        {
            string name = null;

            if (routeValues.Count > 0)
            {
                object controller;
                routeValues.TryGetValue("controller", out controller);
                string controllerString = (controller == null) ? string.Empty : controller.ToString();

                if (!string.IsNullOrEmpty(controllerString))
                {
                    name = controllerString;

                    if (routeValues.TryGetValue("action", out var action) && action != null)
                    {
                        name += "/" + action.ToString();
                    }

                    if (routeValues.Keys.Count > 2)
                    {
                        // Add parameters
                        var sortedKeys = routeValues.Keys
                            .Where(key =>
                                !string.Equals(key, "controller", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(key, "action", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(key, "!__route_group", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                        if (sortedKeys.Length > 0)
                        {
                            string arguments = string.Join(@"/", sortedKeys);
                            name += " [" + arguments + "]";
                        }
                    }
                }
                else
                {
                    object page;
                    routeValues.TryGetValue("page", out page);
                    string pageString = (page == null) ? string.Empty : page.ToString();
                    if (!string.IsNullOrEmpty(pageString))
                    {
                        name = pageString;
                    }
                }
            }

            return name;
        }

        /// <summary>
        /// Diagnostic event handler method for 'Microsoft.AspNetCore.Hosting.HttpRequestIn.Start' event. This is from 2.XX runtime.
        /// </summary>
        public void OnHttpRequestInStart(HttpContext httpContext)
        {
            if (this.client.IsEnabled())
            {
                // It's possible to host multiple apps (ASP.NET Core or generic hosts) in the same process
                // Each of this apps has it's own HostingDiagnosticListener and corresponding Http listener.
                // We should ignore events for all of them except one
                if (!SubscriptionManager.IsActive(this))
                {
                    AspNetCoreEventSource.Instance.NotActiveListenerNoTracking("Microsoft.AspNetCore.Hosting.HttpRequestIn.Start", Activity.Current?.Id);
                    return;
                }

                if (Activity.Current == null)
                {
                    AspNetCoreEventSource.Instance.LogHostingDiagnosticListenerOnHttpRequestInStartActivityNull();
                    return;
                }

                var currentActivity = Activity.Current;
                Activity newActivity = null;
                string sourceAppId = null;
                string originalParentId = currentActivity.ParentId;
                string legacyRootId = null;
                bool traceParentPresent = false;

                // 3 posibilities when TelemetryConfiguration.EnableW3CCorrelation = true
                // 1. No incoming headers. originalParentId will be null. Simply use the Activity as such.
                // 2. Incoming Request-ID Headers. originalParentId will be request-id, but Activity ignores this for ID calculations.
                //    If incoming ID is W3C compatible, ignore current Activity. Create new one with parent set to incoming W3C compatible rootid.  
                //    If incoming ID is not W3C compatible, we can use Activity as such, but need to store originalParentID in custom property 'legacyRootId'
                // 3. Incoming TraceParent header. Need to ignore current Activity, and create new from incoming W3C TraceParent header.

                // Another 3 posibilities when TelemetryConfiguration.EnableW3CCorrelation = false
                // 1. No incoming headers. originalParentId will be null. Simply use the Activity as such.
                // 2. Incoming Request-ID Headers. originalParentId will be request-id, Activity uses this for ID calculations.
                // 3. Incoming TraceParent header. Will simply Ignore W3C headers, and Current Activity used as such.

                // Attempt to find parent from incoming W3C Headers which 2.XX Hosting is unaware of.
                if (currentActivity.IdFormat == ActivityIdFormat.W3C && httpContext.Request.Headers.TryGetValue(W3CConstants.TraceParentHeader, out StringValues traceParentValues)
                     && traceParentValues != StringValues.Empty)
                {
                    var parentTraceParent = StringUtilities.EnforceMaxLength(
                        traceParentValues.First(),
                        InjectionGuardConstants.TraceParentHeaderMaxLength);
                    originalParentId = parentTraceParent;
                    traceParentPresent = true;
                }

                // Scenario #1. No incoming correlation headers.
                if (originalParentId == null)
                {
                    // Nothing to do here. 
                }
                else if(traceParentPresent)
                {
                    // Scenario #3. W3C-TraceParent
                    // We need to ignore the Activity created by Hosting, as it did not take W3CTraceParent into consideration.
                    newActivity = new Activity(ActivityCreatedByHostingDiagnosticListener);
                    newActivity.SetParentId(originalParentId);

                    // read and populate tracestate
                    ReadTraceState(httpContext.Request.Headers, newActivity);
                }
                else 
                {
                    // Scenario #2. RequestID
                    if (currentActivity.IdFormat == ActivityIdFormat.W3C)
                    {
                        var rootIdFromOriginalParentId = ExtractOperationIdFromRequestId(originalParentId);
                        if (IsCompatibleW3CTraceID(rootIdFromOriginalParentId))
                        {
                            newActivity = new Activity(ActivityCreatedByHostingDiagnosticListener);
                            newActivity.SetParentId(ActivityTraceId.CreateFromString(rootIdFromOriginalParentId.AsSpan()), default(ActivitySpanId), ActivityTraceFlags.None);

                            foreach(var bag in currentActivity.Baggage)
                            {
                                newActivity.AddBaggage(bag.Key, bag.Value);
                            }
                        }
                        else
                        {
                            // store rootIdFromOriginalParentId in custom Property
                            legacyRootId = rootIdFromOriginalParentId;
                        }
                    }
                }

                if (newActivity != null)
                {
                    newActivity.Start();
                    currentActivity = newActivity;
                }

                var requestTelemetry = this.InitializeRequestTelemetry(httpContext, currentActivity, Stopwatch.GetTimestamp(), legacyRootId);
                requestTelemetry.Context.Operation.ParentId = originalParentId;

                this.AddAppIdToResponseIfRequired(httpContext, requestTelemetry);
            }
        }

        private string ExtractOperationIdFromRequestId(string originalParentId)
        {
            int indexPipe = originalParentId.IndexOf('|');
            int indexDot = originalParentId.IndexOf('.');
            if (indexPipe>=0 && indexDot >=0)
            {
                return originalParentId.Substring(indexPipe + 1, (indexDot - indexPipe) - 1);
            }
            else
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Diagnostic event handler method for 'Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop' event. This is from 2.XX runtime.
        /// </summary>
        public void OnHttpRequestInStop(HttpContext httpContext)
        {
            EndRequest(httpContext, Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// Diagnostic event handler method for 'Microsoft.AspNetCore.Hosting.BeginRequest' event. This is from 1.XX runtime.
        /// </summary>
        public void OnBeginRequest(HttpContext httpContext, long timestamp)
        {
            if (this.client.IsEnabled() && !this.enableNewDiagnosticEvents)
            {
                // It's possible to host multiple apps (ASP.NET Core or generic hosts) in the same process
                // Each of this apps has it's own HostingDiagnosticListener and corresponding Http listener.
                // We should ignore events for all of them except one
                if (!SubscriptionManager.IsActive(this))
                {
                    AspNetCoreEventSource.Instance.NotActiveListenerNoTracking(
                        "Microsoft.AspNetCore.Hosting.BeginRequest", Activity.Current?.Id);
                    return;
                }

                // 1.XX does not create Activity and SDK is responsible for creating Activity.
                var activity = new Activity(ActivityCreatedByHostingDiagnosticListener);
                string sourceAppId = null;
                IHeaderDictionary requestHeaders = httpContext.Request.Headers;
                string originalParentId = null;
                string legacyRootId = null;

                // W3C-TraceParent
                if (Activity.DefaultIdFormat == ActivityIdFormat.W3C && 
                    requestHeaders.TryGetValue(W3C.W3CConstants.TraceParentHeader, out StringValues traceParentValues) &&
                    traceParentValues != StringValues.Empty)
                {
                    var parentTraceParent = StringUtilities.EnforceMaxLength(traceParentValues.First(), InjectionGuardConstants.TraceParentHeaderMaxLength);
                    originalParentId = parentTraceParent;
                    activity.SetParentId(originalParentId);

                    ReadTraceState(requestHeaders, activity);
                }
                // Request-Id
                else if (requestHeaders.TryGetValue(RequestResponseHeaders.RequestIdHeader, out StringValues requestIdValues) &&
                    requestIdValues != StringValues.Empty)
                {
                    var requestId = StringUtilities.EnforceMaxLength(requestIdValues.First(), InjectionGuardConstants.RequestHeaderMaxLength);
                    originalParentId = requestId;
                    if (Activity.DefaultIdFormat == ActivityIdFormat.W3C)
                    {
                        var rootIdFromOriginalRequestId = ExtractOperationIdFromRequestId(requestId);
                        if (IsCompatibleW3CTraceID(rootIdFromOriginalRequestId))
                        {
                            activity.SetParentId(ActivityTraceId.CreateFromString(rootIdFromOriginalRequestId.AsSpan()), default(ActivitySpanId), ActivityTraceFlags.None);
                        }
                        else
                        {
                            // store rootIdFromOriginalParentId in custom Property inside RequestTelemetry
                            legacyRootId = rootIdFromOriginalRequestId;                           
                        }
                    }
                    else
                    {
                        activity.SetParentId(requestId);
                    }

                    ReadCorrelationContext(requestHeaders, activity);
                }
                // no headers
                else
                {
                    // No need of doing anything. When Activity starts, it'll generate IDs in W3C or Hierrachial format as configured,
                }

                activity.Start();

                var requestTelemetry = this.InitializeRequestTelemetry(httpContext, activity, timestamp, legacyRootId);
                if (this.enableW3CHeaders && sourceAppId != null)
                {
                    requestTelemetry.Source = sourceAppId;
                }

                // fix parent that may be modified by non-W3C operation correlation
                requestTelemetry.Context.Operation.ParentId = originalParentId;

                this.AddAppIdToResponseIfRequired(httpContext, requestTelemetry);
            }
        }

        /// <summary>
        /// Diagnostic event handler method for 'Microsoft.AspNetCore.Hosting.EndRequest' event. This is from 1.XX runtime.
        /// </summary>
        public void OnEndRequest(HttpContext httpContext, long timestamp)
        {
            if (!this.enableNewDiagnosticEvents)
            {
                this.EndRequest(httpContext, timestamp);
            }
        }

        /// <summary>
        /// Diagnostic event handler method for 'Microsoft.AspNetCore.Hosting.UnhandledException' event.
        /// </summary>
        public void OnHostingException(HttpContext httpContext, Exception exception)
        {
            this.OnException(httpContext, exception);

            // In AspNetCore 1.0, when an exception is unhandled it will only send the UnhandledException event, but not the EndRequest event, so we need to call EndRequest here.
            // In AspNetCore 2.0, after sending UnhandledException, it will stop the created activity, which will send HttpRequestIn.Stop event, so we will just end the request there.
            if (!this.enableNewDiagnosticEvents)
            {
                this.EndRequest(httpContext, Stopwatch.GetTimestamp());
            }
        }

        /// <summary>
        /// Diagnostic event handler method for 'Microsoft.AspNetCore.Hosting.HandledException' event.
        /// </summary>
        public void OnDiagnosticsHandledException(HttpContext httpContext, Exception exception)
        {
            this.OnException(httpContext, exception);
        }

        /// <summary>
        /// Diagnostic event handler method for 'Microsoft.AspNetCore.Diagnostics.UnhandledException' event.
        /// </summary>
        public void OnDiagnosticsUnhandledException(HttpContext httpContext, Exception exception)
        {
            this.OnException(httpContext, exception);
        }

        private void AddAppIdToResponseIfRequired(HttpContext httpContext, RequestTelemetry requestTelemetry)
        {
            if (this.conditionalAppIdEnabled)
            {
                // Only reply back with AppId if we got an indication that we need to set one
                if (!string.IsNullOrWhiteSpace(requestTelemetry.Source))
                {
                    this.SetAppIdInResponseHeader(httpContext, requestTelemetry);
                }
            }
            else
            {
                this.SetAppIdInResponseHeader(httpContext, requestTelemetry);
            }
        }

        private static string FormatTelemetryId(string traceId, string spanId)
        {
            return string.Concat("|", traceId, ".", spanId, ".");
        }

        /// <summary>
        /// Checks if the given string is a valid trace-id as per W3C Specs.
        /// https://github.com/w3c/distributed-tracing/blob/master/trace_context/HTTP_HEADER_FORMAT.md#trace-id .
        /// </summary>
        /// <returns>true if valid w3c trace id, otherwise false.</returns>
        private static bool IsCompatibleW3CTraceID(string traceId)
        {
            return TraceIdRegex.IsMatch(traceId);
        }

        private RequestTelemetry InitializeRequestTelemetry(HttpContext httpContext, Activity activity, long timestamp, string legacyRootId = null)
        {
            var requestTelemetry = new RequestTelemetry();

            if (activity.IdFormat == ActivityIdFormat.W3C)
            {
                requestTelemetry.Id = FormatTelemetryId(activity.TraceId.ToHexString(), activity.SpanId.ToHexString());
                requestTelemetry.Context.Operation.Id = activity.TraceId.ToHexString();                
            }
            else
            {
                requestTelemetry.Context.Operation.Id = activity.RootId;
                requestTelemetry.Id = activity.Id;
            }

            if (this.proactiveSamplingEnabled
                && this.configuration != null
                && !string.IsNullOrEmpty(requestTelemetry.Context.Operation.Id)
                && SamplingScoreGenerator.GetSamplingScore(requestTelemetry.Context.Operation.Id) >= this.configuration.GetLastObservedSamplingPercentage(requestTelemetry.ItemTypeFlag))
            {
                requestTelemetry.IsSampledOutAtHead = true;
                AspNetCoreEventSource.Instance.TelemetryItemWasSampledOutAtHead(requestTelemetry.Context.Operation.Id);
            }

            //// When the item is proactively sampled out, we can avoid heavy operations that do not have known dependency later in the pipeline.
            //// We mostly exclude operations that were deemed heavy as per the corresponding profiler trace of this code path.

            if (!requestTelemetry.IsSampledOutAtHead)
            {
                foreach (var prop in activity.Baggage)
                {
                    if (!requestTelemetry.Properties.ContainsKey(prop.Key))
                    {
                        requestTelemetry.Properties[prop.Key] = prop.Value;
                    }
                }

                if (!string.IsNullOrEmpty(legacyRootId))
                {
                    requestTelemetry.Properties[LegacyRootIdProperty] = legacyRootId;
                }
            }

            this.client.InitializeInstrumentationKey(requestTelemetry);
            requestTelemetry.Source = GetAppIdFromRequestHeader(httpContext.Request.Headers, requestTelemetry.Context.InstrumentationKey);

            requestTelemetry.Start(timestamp);
            httpContext.Features.Set(requestTelemetry);

            return requestTelemetry;
        }

        private string GetAppIdFromRequestHeader(IHeaderDictionary requestHeaders, string instrumentationKey)
        {
            // set Source
            string headerCorrelationId = HttpHeadersUtilities.GetRequestContextKeyValue(requestHeaders, RequestResponseHeaders.RequestContextSourceKey);

            // If the source header is present on the incoming request, and it is an external component (not the same ikey as the one used by the current component), populate the source field.
            if (!string.IsNullOrEmpty(headerCorrelationId))
            {
                headerCorrelationId = StringUtilities.EnforceMaxLength(headerCorrelationId, InjectionGuardConstants.AppIdMaxLength);
                if (string.IsNullOrEmpty(instrumentationKey))
                {
                    return headerCorrelationId;
                }

                string applicationId = null;
                if ((this.applicationIdProvider?.TryGetApplicationId(instrumentationKey, out applicationId) ?? false)
                         && applicationId != headerCorrelationId)
                {
                    return headerCorrelationId;
                }
            }

            return null;
        }

        private void SetAppIdInResponseHeader(HttpContext httpContext, RequestTelemetry requestTelemetry)
        {
            if (this.injectResponseHeaders)
            {
                IHeaderDictionary responseHeaders = httpContext.Response?.Headers;
                if (responseHeaders != null &&
                    !string.IsNullOrEmpty(requestTelemetry.Context.InstrumentationKey) &&
                    (!responseHeaders.ContainsKey(RequestResponseHeaders.RequestContextHeader) ||
                     HttpHeadersUtilities.ContainsRequestContextKeyValue(
                         responseHeaders,
                         RequestResponseHeaders.RequestContextTargetKey)))
                {
                    if (this.lastIKeyLookedUp != requestTelemetry.Context.InstrumentationKey)
                    {
                        this.lastIKeyLookedUp = requestTelemetry.Context.InstrumentationKey;
                        this.applicationIdProvider?.TryGetApplicationId(requestTelemetry.Context.InstrumentationKey, out this.lastAppIdUsed);
                    }

                    HttpHeadersUtilities.SetRequestContextKeyValue(responseHeaders, 
                        RequestResponseHeaders.RequestContextTargetKey, this.lastAppIdUsed);
                }
            }
        }

        private void EndRequest(HttpContext httpContext, long timestamp)
        {
            if (this.client.IsEnabled())
            {
                // It's possible to host multiple apps (ASP.NET Core or generic hosts) in the same process
                // Each of this apps has it's own HostingDiagnosticListener and corresponding Http listener.
                // We should ignore events for all of them except one
                if (!SubscriptionManager.IsActive(this))
                {
                    AspNetCoreEventSource.Instance.NotActiveListenerNoTracking(
                        "EndRequest", Activity.Current?.Id);
                    return;
                }

                var telemetry = httpContext?.Features.Get<RequestTelemetry>();

                if (telemetry == null)
                {
                    // Log we are not tracking this request as it cannot be found in context.
                    return;
                }

                telemetry.Stop(timestamp);
                telemetry.ResponseCode = httpContext.Response.StatusCode.ToString(CultureInfo.InvariantCulture);

                var successExitCode = httpContext.Response.StatusCode < 400;
                if (telemetry.Success == null)
                {
                    telemetry.Success = successExitCode;
                }
                else
                {
                    telemetry.Success &= successExitCode;
                }

                if (string.IsNullOrEmpty(telemetry.Name))
                {
                    telemetry.Name = httpContext.Request.Method + " " + httpContext.Request.Path.Value;
                }

                if (!telemetry.IsSampledOutAtHead)
                {
                    telemetry.Url = httpContext.Request.GetUri();
                    telemetry.Context.GetInternalContext().SdkVersion = this.sdkVersion;
                }

                this.client.TrackRequest(telemetry);

                // Stop what we started.
                var activity = Activity.Current; 
                if (activity != null && activity.OperationName == ActivityCreatedByHostingDiagnosticListener)
                {
                    activity.Stop();
                }
            }
        }

        private void OnException(HttpContext httpContext, Exception exception)
        {
            if (this.trackExceptions && this.client.IsEnabled())
            {
                // It's possible to host multiple apps (ASP.NET Core or generic hosts) in the same process
                // Each of this apps has it's own HostingDiagnosticListener and corresponding Http listener.
                // We should ignore events for all of them except one
                if (!SubscriptionManager.IsActive(this))
                {
                    AspNetCoreEventSource.Instance.NotActiveListenerNoTracking(
                        "Exception", Activity.Current?.Id);
                    return;
                }

                var telemetry = httpContext?.Features.Get<RequestTelemetry>();
                if (telemetry != null)
                {
                    telemetry.Success = false;
                }

                var exceptionTelemetry = new ExceptionTelemetry(exception);
                exceptionTelemetry.HandledAt = ExceptionHandledAt.Platform;
                exceptionTelemetry.Context.GetInternalContext().SdkVersion = this.sdkVersion;
                this.client.Track(exceptionTelemetry);
            }
        }

        private void ReadCorrelationContext(IHeaderDictionary requestHeaders, Activity activity)
        {
            string[] baggage = requestHeaders.GetCommaSeparatedValues(RequestResponseHeaders.CorrelationContextHeader);
            if (baggage != StringValues.Empty && !activity.Baggage.Any())
            {
                foreach (var item in baggage)
                {
                    var parts = item.Split('=');
                    if (parts.Length == 2)
                    {
                        var itemName = StringUtilities.EnforceMaxLength(parts[0], InjectionGuardConstants.ContextHeaderKeyMaxLength);
                        var itemValue = StringUtilities.EnforceMaxLength(parts[1], InjectionGuardConstants.ContextHeaderValueMaxLength);
                        activity.AddBaggage(itemName, itemValue);
                    }
                }
            }
        }

        private void ReadTraceState(IHeaderDictionary requestHeaders, Activity activity)
        {
            if (requestHeaders.TryGetValue(W3CConstants.TraceStateHeader, out var traceState))
            {
                // SDK is not relying on anything from tracestate.
                // It simply sets activity tracestate, so that outbound calls
                // make in the request context can continue propogation
                // of tracestate.
                activity.TraceStateString = traceState;
            }
        }

        public void Dispose()
        {
            SubscriptionManager.Detach(this);
        }

        public void OnNext(KeyValuePair<string, object> value)
        {
            HttpContext httpContext = null;
            Exception exception = null;
            long? timestamp = null;

            //// Top messages in if-else are the most often used messages.
            //// It starts with ASP.NET Core 2.0 events, then 1.0 events, then exception events.
            //// Switch is compiled into GetHashCode() and binary search, if-else without GetHashCode() is faster if 2.0 events are used.
            if (value.Key == "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start")
            {
                httpContext = this.httpContextFetcherStart.Fetch(value.Value) as HttpContext;
                if (httpContext != null)
                {
                    this.OnHttpRequestInStart(httpContext);
                }
            }
            else if (value.Key == "Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop")
            {
                httpContext = this.httpContextFetcherStop.Fetch(value.Value) as HttpContext;
                if (httpContext != null)
                {
                    this.OnHttpRequestInStop(httpContext);
                }
            }
            else if (value.Key == "Microsoft.AspNetCore.Mvc.BeforeAction")
            {
                var context = this.httpContextFetcherOnBeforeAction.Fetch(value.Value) as HttpContext;
                var routeData = this.routeDataFetcher.Fetch(value.Value);
                var routeValues = this.routeValuesFetcher.Fetch(routeData) as IDictionary<string, object>;

                if (context != null && routeValues != null)
                {
                    this.OnBeforeAction(context, routeValues);
                }

            }
            else if (value.Key == "Microsoft.AspNetCore.Hosting.BeginRequest")
            {
                httpContext = this.httpContextFetcherBeginRequest.Fetch(value.Value) as HttpContext;
                timestamp = this.timestampFetcherBeginRequest.Fetch(value.Value) as long?;
                if (httpContext != null && timestamp.HasValue)
                {
                    this.OnBeginRequest(httpContext, timestamp.Value);
                }
            }
            else if (value.Key == "Microsoft.AspNetCore.Hosting.EndRequest")
            {
                httpContext = this.httpContextFetcherEndRequest.Fetch(value.Value) as HttpContext;
                timestamp = this.timestampFetcherEndRequest.Fetch(value.Value) as long?;
                if (httpContext != null && timestamp.HasValue)
                {
                    this.OnEndRequest(httpContext, timestamp.Value);
                }
            }
            else if (value.Key == "Microsoft.AspNetCore.Diagnostics.UnhandledException")
            {
                httpContext = this.httpContextFetcherDiagExceptionUnhandled.Fetch(value.Value) as HttpContext;
                exception = this.exceptionFetcherDiagExceptionUnhandled.Fetch(value.Value) as Exception;
                if (httpContext != null && exception != null)
                {
                    this.OnDiagnosticsUnhandledException(httpContext, exception);
                }
            }
            else if (value.Key == "Microsoft.AspNetCore.Diagnostics.HandledException")
            {
                httpContext = this.httpContextFetcherDiagExceptionHandled.Fetch(value.Value) as HttpContext;
                exception = this.exceptionFetcherDiagExceptionHandled.Fetch(value.Value) as Exception;
                if (httpContext != null && exception != null)
                {
                    this.OnDiagnosticsHandledException(httpContext, exception);
                }
            }
            else if (value.Key == "Microsoft.AspNetCore.Hosting.UnhandledException")
            {
                httpContext = this.httpContextFetcherHostingExceptionUnhandled.Fetch(value.Value) as HttpContext;
                exception = this.exceptionFetcherHostingExceptionUnhandled.Fetch(value.Value) as Exception;
                if (httpContext != null && exception != null)
                {
                    this.OnHostingException(httpContext, exception);
                }
            }
        }

        /// <inheritdoc />
        public void OnError(Exception error)
        {
        }

        /// <inheritdoc />
        public void OnCompleted()
        {
        }

    }
}