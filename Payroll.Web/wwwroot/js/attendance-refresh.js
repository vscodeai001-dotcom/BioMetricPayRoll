window.attendanceRefresh = (function () {

    let connection = null;
    let started = false;
    let starting = false;
    let retryTimer = null;

    let viewerRef = null;
    let viewerRefreshTimer = null;
    let viewerRefreshInFlight = false;
    let viewerRefreshPending = false;
    let listeners = [];
    let applicationListeners = [];

    async function start() {

        if (started || starting)
            return;

        if (!window.signalR) {
    console.warn("Attendance refresh: SignalR client missing. Attempting dynamic load...");
    
    // Check if script element is already injected
    if (!document.getElementById("signalr-client-script")) {
        const script = document.createElement("script");
        script.id = "signalr-client-script";
        // Update URL to your local lib or CDN
        script.src = "https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js"; 
        script.onload = function () {
            console.log("SignalR client loaded successfully.");
            start();
        };
        script.onerror = function () {
            console.error("Failed to load SignalR client script.");
            setTimeout(start, 5000);
        };
        document.head.appendChild(script);
    } else {
        setTimeout(start, 1000);
    }
    return;
}

        starting = true;

        try {

            connection =
                new signalR.HubConnectionBuilder()
                    .withUrl("/hubs/attendance-refresh")
                    .withAutomaticReconnect([
                        0,
                        2000,
                        5000,
                        10000,
                        30000
                    ])
                    .configureLogging(
                        signalR.LogLevel.Warning
                    )
                    .build();

            // Explicitly align the browser connection with the server
            // keep-alive/timeout policy. This is especially important
            // behind Render's reverse proxy.
            connection.keepAliveIntervalInMilliseconds = 15000;
            connection.serverTimeoutInMilliseconds = 60000;


            /*
             * ==========================================================
             * GENERIC DATA CHANGED
             * ==========================================================
             */

            connection.on(
                "DataChanged",
                async function (data) {

                    console.log(
                        "Attendance DataChanged",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "AttendanceChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "attendance-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );

            /*
             * ==========================================================
             * APPLICATION-WIDE DATABASE CHANGE
             * ==========================================================
             *
             * Emitted centrally after a successful EF Core write.
             * This is a database invalidation signal, not a data
             * payload. The active route reloads from the database.
             *
             * Rapid writes are coalesced so bulk CRUD does not cause
             * a refresh storm.
             */
            connection.on(
                "ApplicationDataChanged",
                async function (data) {

                    console.log(
                        "ApplicationDataChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "application-data-changed",
                            {
                                detail: data
                            }
                        )
                    );

                    // Deliver the application-wide invalidation immediately.
                    // Database writes are already the source of truth; there is
                    // intentionally no artificial debounce here.
                    await notifyApplicationListeners(data);

                }
            );


            // LOCATION HEALTH (periodic status of sessions)
            // This is a browser-side health event only. It is deliberately
            // not converted into LocationChanged, which is reserved for
            // actual GPS position updates. This avoids a 30-second Blazor
            // refresh storm and keeps the realtime circuit lighter.
            connection.on(
                "LocationHealth",
                function (data) {
                    console.log('LocationHealth', data);
                    window.dispatchEvent(
                        new CustomEvent(
                            'location-health-updated',
                            { detail: data }
                        )
                    );
                }
            );

            // Match the server-side geography settings event so SignalR
            // never reports "No client method ... found" for this event.
            connection.on(
                "GeoSettingsChanged",
                function (data) {
                    console.log(
                        "GeoSettingsChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            'geo-settings-changed',
                            { detail: data }
                        )
                    );
                }
            );

            connection.on(
                "LeaveChanged",
                async function (data) {

                    console.log(
                        "LeaveChanged",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "AttendanceChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "attendance-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );

            connection.on(
                "AdvanceChanged",
                async function (data) {

                    console.log(
                        "AdvanceChanged",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "AttendanceChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "attendance-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );

            connection.on(
                "PunchChanged",
                async function (data) {

                    console.log(
                        "PunchChanged",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "AttendanceChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "attendance-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );

            connection.on(
                "EmployeeChanged",
                async function (data) {

                    console.log(
                        "EmployeeChanged",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "AttendanceChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "attendance-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );

            connection.on(
                "ExitChanged",
                async function (data) {

                    console.log(
                        "ExitChanged",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "AttendanceChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "attendance-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );

            connection.on(
                "GlobalRefresh",
                async function (data) {

                    console.log(
                        "GlobalRefresh",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "AttendanceChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "attendance-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );


            /*
             * ==========================================================
             * ATTENDANCE CHANGED
             * ==========================================================
             */

            connection.on(
                "AttendanceChanged",
                async function (data) {

                    console.log(
                        "AttendanceChanged",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "AttendanceChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "attendance-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );


            /*
             * ==========================================================
             * LOCATION CHANGED
             * ==========================================================
             *
             * Employee GPS sends:
             *
             * Employee
             *     ↓
             * LiveLocationStore
             *     ↓
             * SignalR
             *     ↓
             * LocationChanged
             *     ↓
             * Admin listeners
             *
             */

            /*
             * ==========================================================
             * GPS SESSION LIFECYCLE
             * ==========================================================
             *
             * A logout does not produce a LocationChanged event.
             * Therefore an admin screen must receive the explicit
             * SessionEnded event or it can retain the last live card.
             */

            connection.on(
                "SessionEnded",
                async function (data) {

                    console.log(
                        "SessionEnded",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "gps-session-ended",
                            {
                                detail: data
                            }
                        )
                    );

                    await notifyListeners(
                        "SessionEnded",
                        data
                    );

                    // Also notify application-wide listeners so any device is kicked out
                    await notifyApplicationListeners(
                        "SessionEnded",
                        data
                    );
                }
            );

            connection.on(
                "SessionStarted",
                async function (data) {

                    console.log(
                        "SessionStarted",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "gps-session-started",
                            {
                                detail: data
                            }
                        )
                    );

                    await notifyListeners(
                        "SessionStarted",
                        data
                    );
                }
            );


            connection.on(
                "LocationChanged",
                async function (data) {

                    console.log(
                        "LocationChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "location-data-changed",
                            {
                                detail: data
                            }
                        )
                    );

                    /*
                     * IMPORTANT:
                     * Pass the location payload through.
                     *
                     * The current LiveStaffLocationPanel can still
                     * reload LiveLocationStore, so this remains
                     * backward compatible.
                     */
                    await notifyListeners(
                        "LocationChanged",
                        data
                    );
                }
            );


            /*
             * ==========================================================
             * REGULARIZATION CHANGED
             * ==========================================================
             */

            connection.on(
                "RegularizationChanged",
                async function (data) {

                    console.log(
                        "RegularizationChanged",
                        data
                    );

                    await notifyViewer();

                    await notifyListeners(
                        "RegularizationChanged",
                        data
                    );

                    window.dispatchEvent(
                        new CustomEvent(
                            "regularization-data-changed",
                            {
                                detail: data
                            }
                        )
                    );
                }
            );

            connection.on(
                "NotificationChanged",
                async function (data) {
                    await notifyListeners(
                        "NotificationChanged",
                        data
                    );
                }
            );


            /*
             * ==========================================================
             * RECONNECTING
             * ==========================================================
             */

            connection.onreconnecting(
                function () {

                    console.log(
                        "Attendance refresh connection reconnecting..."
                    );
                }
            );


            /*
             * ==========================================================
             * RECONNECTED
             * ==========================================================
             */

            connection.onreconnected(
                async function (connectionId) {

                    console.log(
                        "Attendance refresh connection restored.",
                        connectionId
                    );

                    /*
                     * Refresh normal attendance viewers.
                     */
                    await notifyViewer();

                    /*
                     * IMPORTANT:
                     *
                     * Refresh all live-location listeners too.
                     *
                     * This allows the admin map to recover the latest
                     * in-memory employee positions after reconnect.
                     */
                    await notifyListeners(
                        "LocationChanged",
                        null
                    );

                    await notifyApplicationListeners(
                        {
                            Entities: [],
                            Reason: "SIGNALR_RECONNECTED"
                        }
                    );
                }
            );


            /*
             * ==========================================================
             * CLOSED
             * ==========================================================
             */

            connection.onclose(
                function () {

                    started = false;
                    starting = false;

                    console.warn(
                        "Attendance refresh connection closed."
                    );

                    scheduleRetry();
                }
            );


            await connection.start();

            started = true;

            console.log(
                "Attendance refresh connection started."
            );

        }
        catch (error) {

            console.error(
                "Unable to start attendance refresh:",
                error
            );

            started = false;

            try {

                if (connection) {
                    await connection.stop();
                }

            }
            catch {
            }

            connection = null;

            scheduleRetry();
        }
        finally {

            starting = false;
        }
    }

    function scheduleRetry() {

        if (retryTimer || (!listeners.length && !applicationListeners.length))
            return;

        retryTimer = setTimeout(
            function () {
                retryTimer = null;
                start();
            },
            2000
        );
    }


    /*
     * ==============================================================
     * APPLICATION-WIDE LISTENER NOTIFICATION
     * ==============================================================
     */

    async function notifyApplicationListeners(methodName, data) {

        const currentListeners =
            [...applicationListeners];

        for (const listener of currentListeners) {

            try {
                // If only one argument is passed, default to ApplicationDataChanged for backward compatibility
                const targetMethod = typeof data === "undefined" ? "ApplicationDataChanged" : (typeof methodName === "string" ? methodName : "ApplicationDataChanged");
                const payload = typeof data === "undefined" ? methodName : data;

                await listener.invokeMethodAsync(
                    targetMethod,
                    payload
                );
            }
            catch (error) {
                applicationListeners = applicationListeners.filter(function (item) {
                    return item !== listener;
                });
            }
        }
    }


    /*
     * ==============================================================
     * VIEWER NOTIFICATION
     * ==============================================================
     */

    async function notifyViewer() {

        if (!viewerRef)
            return;

        // Attendance can generate several SignalR notifications in a very
        // short period. Debounce them so the Blazor component receives one
        // controlled refresh instead of a burst of concurrent JS -> .NET
        // invocations.
        viewerRefreshPending = true;

        if (viewerRefreshTimer !== null) {
            clearTimeout(viewerRefreshTimer);
        }

        viewerRefreshTimer = setTimeout(
            flushViewerRefresh,
            350
        );
    }


    async function flushViewerRefresh() {

        viewerRefreshTimer = null;

        if (viewerRefreshInFlight || !viewerRefreshPending)
            return;

        if (!viewerRef) {
            viewerRefreshPending = false;
            return;
        }

        viewerRefreshPending = false;
        viewerRefreshInFlight = true;

        const currentViewerRef = viewerRef;

        try {

            await currentViewerRef.invokeMethodAsync(
                "RefreshFromNotification"
            );

        }
        catch (error) {

            // A disposed Blazor component can remain referenced briefly while
            // SignalR is delivering an event. Clear only that stale reference
            // so future notifications do not repeatedly invoke a dead circuit.
            if (viewerRef === currentViewerRef) {
                viewerRef = null;
            }

            console.debug(
                "Attendance viewer refresh skipped because the viewer is no longer available.",
                error
            );
        }
        finally {

            viewerRefreshInFlight = false;

            // If another event arrived while the previous refresh was running,
            // schedule exactly one follow-up refresh.
            if (viewerRefreshPending && viewerRef) {
                viewerRefreshTimer = setTimeout(
                    flushViewerRefresh,
                    100
                );
            }
        }
    }


    /*
     * ==============================================================
     * LISTENER NOTIFICATION
     * ==============================================================
     *
     * data is optional.
     *
     * Existing components that define:
     *
     * LocationChanged()
     *
     * continue to work.
     *
     * Components that define:
     *
     * LocationChanged(data)
     *
     * can now receive the actual event payload.
     */

    async function notifyListeners(
        methodName,
        data
    ) {

        const currentListeners =
            [...listeners];

        for (const listener of currentListeners) {

            try {

                if (typeof data === "undefined") {

                    await listener.invokeMethodAsync(
                        methodName
                    );

                }
                else {

                    await listener.invokeMethodAsync(
                        methodName,
                        data
                    );

                }

            }
            catch (error) {

                // A Blazor component can disappear while SignalR is still
                // delivering an event. In that case the DotNetObjectReference
                // is stale and every future realtime event would fail again.
                //
                // Keep the existing callback/fallback behavior, but remove
                // the reference only when BOTH calls fail. This is lifecycle
                // cleanup only and does not change any business logic.

                let callbackFailed = true;

                if (typeof data !== "undefined") {
                    try {
                        await listener.invokeMethodAsync(
                            methodName
                        );

                        callbackFailed = false;
                    }
                    catch (fallbackError) {
                        console.debug(
                            "Attendance refresh listener became unavailable; removing stale listener.",
                            methodName
                        );
                    }
                }

                if (callbackFailed) {
                    const index = listeners.indexOf(listener);

                    if (index >= 0) {
                        listeners.splice(index, 1);
                    }
                }

            }
        }
    }


    /*
     * ==============================================================
     * VIEWER REGISTRATION
     * ==============================================================
     */

    function registerViewer(dotNetReference) {

        viewerRef = dotNetReference;

        start();
    }


    async function unregisterViewer(
        dotNetReference
    ) {

        if (viewerRef === dotNetReference) {
            viewerRef = null;
        }

        viewerRefreshPending = false;

        if (viewerRefreshTimer !== null) {
            clearTimeout(viewerRefreshTimer);
            viewerRefreshTimer = null;
        }
    }


    /*
     * ==============================================================
     * GENERAL LISTENER REGISTRATION
     * ==============================================================
     */

    function register(dotNetReference) {

        if (!listeners.includes(dotNetReference)) {

            listeners.push(
                dotNetReference
            );
        }

        start();
    }


    async function unregister(
        dotNetReference
    ) {

        listeners =
            listeners.filter(
                function (item) {

                    return item !== dotNetReference;
                }
            );
    }

    /*
     * ==============================================================
     * APPLICATION-WIDE LISTENER REGISTRATION
     * ==============================================================
     */

    function registerApplication(dotNetReference) {

        if (!applicationListeners.includes(dotNetReference)) {
            applicationListeners.push(dotNetReference);
        }

        start();
    }

    async function unregisterApplication(dotNetReference) {

        applicationListeners =
            applicationListeners.filter(
                function (item) {
                    return item !== dotNetReference;
                }
            );

        // No application-wide debounce timer is used.
    }


    // Compatibility API retained for existing components. LocationHealth
    // is now handled entirely in the browser and must not invoke a Blazor
    // callback every 30 seconds.
    function registerLocationHealth(dotNetReference) {
        return true;
    }

    function unregisterLocationHealth(dotNetReference) {
        return true;
    }

    return {

        start: start,

        registerViewer:
            registerViewer,

        unregisterViewer:
            unregisterViewer,

        register:
            register,

        unregister:
            unregister,

        registerApplication:
            registerApplication,

        unregisterApplication:
            unregisterApplication

    };



})();