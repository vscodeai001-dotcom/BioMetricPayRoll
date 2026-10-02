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
    // Each application listener may optionally subscribe to one entity.
    // Keeping the filter in the browser avoids unnecessary Blazor reloads
    // when unrelated domains publish application events.
    let applicationListeners = [];

    let applicationRefreshTimer = null;
    let applicationRefreshPending = new Map();

    // Firebase is an independent realtime transport. SignalR remains the
    // existing compatibility path, but Firebase keeps live GPS and CRUD
    // invalidation flowing when the Payroll.Web process is temporarily absent.
    let firebaseStarted = false;
    let firebaseStarting = false;
    let firebaseDatabase = null;
    let firebaseGeoPunchAuditRef = null;
    let firebaseGeoPunchAuditTimer = null;
    let firebaseGeoPunchAuditPending = new Map();
    let firebaseStartTime = Date.now();

    async function startFirebaseRealtime() {
        if (firebaseStarted || firebaseStarting || !window.firebase)
            return;

        firebaseStarting = true;
        firebaseStartTime = Date.now();

        try {
            const response = await fetch('/api/firebase/auth-token', {
                method: 'GET',
                credentials: 'same-origin',
                cache: 'no-store'
            });

            if (!response.ok) {
                console.warn(
                    'Firebase realtime auth endpoint returned HTTP ' +
                    response.status +
                    '. Retrying without requiring a page refresh.'
                );
                scheduleRetry();
                return;
            }

            const authResult = await response.json();
            if (!authResult || !authResult.token) {
                console.warn(
                    'Firebase realtime auth token was empty. Retrying without requiring a page refresh.'
                );
                scheduleRetry();
                return;
            }

            if (!firebase.apps.length) {
                firebase.initializeApp({
                    apiKey: authResult.apiKey || 'AIzaSyDE6qAFRWzKkZiH2G2Hr6a6GC98wjEzucg',
                    authDomain: 'biometricpayroll.firebaseapp.com',
                    databaseURL: 'https://biometricpayroll-default-rtdb.asia-southeast1.firebasedatabase.app',
                    projectId: 'biometricpayroll',
                    storageBucket: 'biometricpayroll.firebasestorage.app',
                    messagingSenderId: '63802944560'
                });
            }

            await firebase.auth().signInWithCustomToken(authResult.token);
            firebaseDatabase = firebase.database();
            firebaseStarted = true;

            console.log(
                'Firebase realtime transport authenticated. Live GPS listener is active.'
            );

            // Use the same owner-scoped live-location branch consumed by the
            // Android Admin app. The legacy tracking/live branch is only a
            // fallback for installations that do not return an owner UID.
            const ownerUid = authResult.ownerUid || authResult.ownerUID || null;
            const realtimeOwnerUid = ownerUid || 'biometricpayroll';
            const liveRef = ownerUid
                ? firebaseDatabase.ref('owners/' + ownerUid + '/tracking/live')
                : firebaseDatabase.ref('tracking/live');
            liveRef.on('child_added', onFirebaseLiveLocation);
            liveRef.on('child_changed', onFirebaseLiveLocation);
            liveRef.on('child_removed', onFirebaseLiveLocationRemoved);
            if (ownerUid) {
                const ownerEventsRef = firebaseDatabase.ref('owner_events/' + ownerUid).limitToLast(50);
                ownerEventsRef.on('child_added', onFirebaseApplicationEvent);

                // Mobile-originated changes use a per-employee channel so an
                // employee cannot write to the shared admin event stream.
                // Admin/SuperAdmin Firebase rules allow the web dashboard to
                // receive these events without changing the existing UI.
                const clientEventsRef = firebaseDatabase.ref('client_events').limitToLast(50);
                clientEventsRef.on('child_added', onFirebaseClientEventEmployee);

            } else {
                // Backward compatibility for installations that have not yet
                // configured a shared Firebase owner UID.
                const eventsRef = firebaseDatabase.ref('application_events').limitToLast(50);
                eventsRef.on('child_added', onFirebaseApplicationEvent);
            }

            // Geo punch audits are always scoped to the same owner as the
            // Firebase auth contract. This remains active even when the auth
            // response omits ownerUid and the single-owner fallback is used.
            firebaseGeoPunchAuditRef = firebaseDatabase.ref(
                'owners/' + realtimeOwnerUid + '/geo_punch_audits'
            );
            firebaseGeoPunchAuditRef.on('child_added', onFirebaseGeoPunchAudit);
            firebaseGeoPunchAuditRef.on('child_changed', onFirebaseGeoPunchAudit);

            // Listen to company_settings for real-time geofence & radar updates
            const companySettingsRef = firebaseDatabase.ref(
                'owners/' + realtimeOwnerUid + '/company_settings/1'
            );
            companySettingsRef.on('value', onFirebaseCompanySettings);

            console.log('Firebase realtime transport connected.');
        } catch (error) {
            console.warn(
                'Firebase realtime transport unavailable. Retrying automatically.',
                error
            );
            scheduleRetry();
        } finally {
            firebaseStarting = false;
        }
    }

    async function onFirebaseLiveLocationRemoved(snapshot) {
        try {
            const data = snapshot.val() || {};
            const employeeId = Number(data.EmployeeId ?? data.employeeId ?? snapshot.key ?? 0);
            if (!Number.isFinite(employeeId) || employeeId <= 0) return;

            const ended = {
                EmployeeId: employeeId,
                SessionId: data.SessionId ?? data.sessionId ?? '',
                EndedAtUtc: new Date().toISOString(),
                EndReason: data.EndReason ?? data.endReason ?? 'FIREBASE_LIVE_REMOVED'
            };

            window.dispatchEvent(new CustomEvent('location-data-removed', { detail: ended }));
            await notifyListeners('SessionEnded', ended);
        } catch (error) {
            console.warn('Firebase live-location removal callback failed.', error);
        }
    }

    async function onFirebaseLiveLocation(snapshot) {
        try {
            const data = snapshot.val();
            if (!data) return;

            const employeeId = Number(data.EmployeeId ?? data.employeeId ?? snapshot.key ?? 0);
            if (!Number.isFinite(employeeId) || employeeId <= 0) return;

            // Normalize properties so all consumers (camelCase and PascalCase) receive valid numbers
            data.EmployeeId = employeeId;
            data.employeeId = employeeId;
            if (data.Latitude !== undefined && data.latitude === undefined) data.latitude = data.Latitude;
            if (data.latitude !== undefined && data.Latitude === undefined) data.Latitude = data.latitude;
            if (data.Longitude !== undefined && data.longitude === undefined) data.longitude = data.Longitude;
            if (data.longitude !== undefined && data.Longitude === undefined) data.Longitude = data.longitude;

            // Dispatch the browser event first. The Leaflet map can therefore
            // react immediately even if a Blazor circuit is busy reconnecting.
            window.dispatchEvent(new CustomEvent('location-data-changed', { detail: data }));
            window.dispatchEvent(new CustomEvent('firebase-location-changed', { detail: data }));

            // Keep the existing Blazor state synchronization path as well.
            await notifyListeners('LocationChanged', data);
        } catch (error) {
            console.warn('Firebase live location callback failed.', error);
        }
    }

    async function onFirebaseGeoPunchAudit(snapshot) {
        try {
            const data = snapshot.val();
            if (!data || typeof data !== 'object') return;

            // Collapse a burst of Firebase child callbacks into one UI
            // invalidation per employee. The existing database query remains
            // authoritative, so no employee update is lost.
            const employeeId = Number(data.employeeId ?? data.EmployeeId ?? 0);
            const pendingKey = Number.isFinite(employeeId) && employeeId > 0
                ? String(employeeId)
                : snapshot.key || String(Date.now());
            firebaseGeoPunchAuditPending.set(pendingKey, data);

            if (firebaseGeoPunchAuditTimer !== null)
                clearTimeout(firebaseGeoPunchAuditTimer);

            firebaseGeoPunchAuditTimer = setTimeout(async function () {
                firebaseGeoPunchAuditTimer = null;
                const pendingEvents = Array.from(firebaseGeoPunchAuditPending.values());
                firebaseGeoPunchAuditPending.clear();

                for (const pending of pendingEvents) {
                    await notifyListeners('GeoPunchAuditChanged', pending);
                    window.dispatchEvent(new CustomEvent('geo-punch-audit-changed', { detail: pending }));
                }
            }, 80);
        } catch (error) {
            console.warn('Firebase geo punch audit callback failed.', error);
        }
    }

    async function onFirebaseClientEventEmployee(employeeSnapshot) {
        try {
            employeeSnapshot.ref.on('child_added', onFirebaseApplicationEvent);
        } catch (error) {
            console.warn('Firebase client-event callback failed.', error);
        }
    }

    async function onFirebaseApplicationEvent(snapshot) {
        try {
            const data = snapshot.val();
            if (!data || !data.timestamp) return;

            const eventTime = Date.parse(data.timestamp);
            // Ignore the initial backlog when the page first attaches. Only
            // changes that happened after this browser session started are
            // realtime invalidations.
            if (Number.isFinite(eventTime) && eventTime + 5000 < firebaseStartTime)
                return;

            // Coalesce bursts from one Firebase write into a single UI
            // invalidation. The event itself remains in Firebase as the audit
            // source; this timer only controls how often Blazor reloads.
            const changes = Array.isArray(data.changes) ? data.changes : [];
            const key = changes.map(function (c) {
                return String(c && (c.Entity || c.entity) || '') + ':' +
                    String(c && (c.RecordId || c.recordId) || '') + ':' +
                    String(c && (c.Action || c.action) || '');
            }).sort().join('|') || snapshot.key || String(Date.now());

            applicationRefreshPending.set(key, data);
            if (applicationRefreshTimer !== null)
                clearTimeout(applicationRefreshTimer);

            applicationRefreshTimer = setTimeout(async function () {
                applicationRefreshTimer = null;
                const pending = Array.from(applicationRefreshPending.values());
                applicationRefreshPending.clear();

                for (const eventData of pending) {
                    await notifyApplicationListeners('ApplicationDataChanged', eventData);
                    await notifyListeners('ApplicationDataChanged', eventData);
                    window.dispatchEvent(new CustomEvent('application-data-changed', { detail: eventData }));

                    if (applicationEventContainsEntity(eventData, 'AdvancePayment') || applicationEventContainsEntity(eventData, 'SalaryAdvance')) {
                        await notifyListeners('AdvanceChanged', eventData);
                        window.dispatchEvent(new CustomEvent('advance-changed', { detail: eventData }));
                    }
                    if (applicationEventContainsEntity(eventData, 'BonusRecord') || applicationEventContainsEntity(eventData, 'Bonus')) {
                        await notifyListeners('BonusChanged', eventData);
                        window.dispatchEvent(new CustomEvent('bonus-changed', { detail: eventData }));
                    }
                    if (applicationEventContainsEntity(eventData, 'LeaveRequest') || applicationEventContainsEntity(eventData, 'Leave')) {
                        await notifyListeners('LeaveChanged', eventData);
                        window.dispatchEvent(new CustomEvent('leave-changed', { detail: eventData }));
                    }

                    // The Attendance Log Viewer is a route-level consumer.
                    // Firebase application events are the realtime source for
                    // Web-side SSOT invalidation, so bridge only attendance-
                    // relevant entities into the viewer's existing debounced
                    // RefreshFromNotification path. This preserves the current
                    // load/recalculation boundary while removing dependence on
                    // a separate SignalR attendance event for Firebase writes.
                    if (viewerRef && applicationEventAffectsAttendanceViewer(eventData)) {
                        await notifyViewer();
                    }
                }
            }, 80);
        } catch (error) {
            console.warn('Firebase application event callback failed.', error);
        }
    }

    async function start() {
        if (starting) return;
        starting = true;

        try {
            /*
             * ==========================================================
             * SIGNALR IS OPTIONAL
             * ==========================================================
             *
             * Firebase is the authoritative realtime transport for the
             * current Firebase-SSOT architecture.
             *
             * Some deployments block the external SignalR JavaScript CDN
             * (or the CDN can be temporarily unavailable). In that case
             * window.signalR does not exist. The old implementation tried
             * to execute:
             *
             *     new signalR.HubConnectionBuilder()
             *
             * which caused:
             *
             *     ReferenceError: signalR is not defined
             *
             * and prevented Firebase realtime from starting.
             *
             * Therefore SignalR is treated as an optional compatibility
             * channel. Firebase must always be allowed to start independently.
             */
            if (!started) {
                if (typeof window.signalR !== "undefined" &&
                    window.signalR &&
                    typeof window.signalR.HubConnectionBuilder === "function") {

                    try {
                        if (!connection) {
                            connection = new window.signalR.HubConnectionBuilder()
                                .withUrl("/hubs/attendance-refresh")
                                .withAutomaticReconnect()
                                .build();

                            connection.on("DataChanged", onDataChanged);
                            connection.on("AttendanceChanged", onAttendanceChanged);
                            connection.on("PunchChanged", onPunchChanged);
                            connection.on("LocationChanged", onLocationChanged);
                            connection.on("SessionStarted", onSessionStarted);
                            connection.on("SessionEnded", onSessionEnded);
                            connection.on("GeoSettingsChanged", onGeoSettingsChanged);
                            connection.on("GeoPunchAuditChanged", onGeoPunchAuditChanged);
                            connection.on("ApplicationDataChanged", onApplicationDataChanged);
                            connection.on("RegularizationChanged", onRegularizationChanged);
                            connection.on("LeaveChanged", onLeaveChanged);
                            connection.on("AdvanceChanged", onAdvanceChanged);
                            connection.on("BonusChanged", onBonusChanged);
                            connection.on("TaxDeclarationChanged", onTaxDeclarationChanged);
                            connection.on("EmployeeChanged", onEmployeeChanged);
                            connection.on("ExitChanged", onExitChanged);
                            connection.on("GlobalRefresh", onGlobalRefresh);
                            connection.on("SessionInvalidated", onSessionInvalidated);

                            connection.onreconnected(function (connectionId) {
                                console.log("SignalR realtime reconnected:", connectionId);
                                notifyListeners('AttendanceChanged', {});
                                notifyViewer();
                            });
                            connection.onclose(function (error) {
                                console.warn("SignalR connection closed. Scheduling auto-reconnect...", error);
                                scheduleRetry();
                            });

                            if (!window.__payrollRealtimeHeartbeatBound) {
                                window.__payrollRealtimeHeartbeatBound = true;
                                function checkAndHealConnection() {
                                    if (!firebaseStarted && !firebaseStarting) {
                                        startFirebaseRealtime();
                                    } else if (firebaseStarted && window.firebase && firebase.auth().currentUser) {
                                        firebase.auth().currentUser.getIdToken(false).catch(function () {
                                            firebaseStarted = false;
                                            startFirebaseRealtime();
                                        });
                                    }
                                    if (connection) {
                                        if (connection.state === window.signalR.HubConnectionState.Disconnected) {
                                            console.log("Auto-healing disconnected SignalR connection...");
                                            connection.start().then(function () {
                                                notifyListeners('AttendanceChanged', {});
                                                notifyViewer();
                                            }).catch(scheduleRetry);
                                        } else if (connection.state === window.signalR.HubConnectionState.Connected) {
                                            notifyViewer();
                                        }
                                    }
                                }
                                window.addEventListener("visibilitychange", function () {
                                    if (document.visibilityState === "visible") checkAndHealConnection();
                                });
                                window.addEventListener("focus", checkAndHealConnection);
                                window.addEventListener("online", checkAndHealConnection);
                                setInterval(checkAndHealConnection, 60000);
                            }
                        }

                        if (connection.state !== window.signalR.HubConnectionState.Connected) {
                            await connection.start();
                        }

                        console.log("SignalR compatibility transport connected.");
                    } catch (signalRError) {
                        /*
                         * Do NOT abort realtime startup.
                         * Firebase below is the primary realtime transport.
                         */
                        console.warn(
                            "SignalR compatibility transport unavailable. Continuing with Firebase realtime.",
                            signalRError
                        );
                    }
                } else {
                    /*
                     * The SignalR CDN is unavailable/not loaded.
                     * This is expected to be recoverable because Firebase
                     * provides the realtime SSOT transport.
                     */
                    console.info(
                        "SignalR JavaScript client is not loaded. Continuing with Firebase realtime."
                    );
                }

                /*
                 * Mark the realtime bootstrap as attempted. This prevents
                 * every Blazor component registration from repeatedly trying
                 * to construct a missing SignalR client.
                 */
                started = true;
            }

            /*
             * Firebase must start independently of SignalR.
             */
            if (!firebaseStarted) {
                await startFirebaseRealtime();
            }

        } catch (err) {
            /*
             * Firebase/auth/network failures are retryable. Do not create
             * a tight retry loop when SignalR alone is unavailable.
             */
            console.warn(
                "Realtime Firebase startup failed. Retrying automatically.",
                err
            );

            /*
             * Allow the next retry to attempt Firebase again.
             * SignalR remains optional and is not allowed to block startup.
             */
            started = true;
            scheduleRetry();
        } finally {
            starting = false;
        }
    }

    function onFirebaseCompanySettings(snapshot) {
        try {
            const val = snapshot.val();
            if (!val || typeof val !== 'object') return;

            const lat = Number(val.officeLatitude ?? val.OfficeLatitude ?? val.latitude ?? val.Latitude ?? 0);
            const lng = Number(val.officeLongitude ?? val.OfficeLongitude ?? val.longitude ?? val.Longitude ?? 0);
            const rad = Number(val.geoRadiusMeters ?? val.GeoRadiusMeters ?? val.radius ?? val.Radius ?? 0);
            const speed = Boolean(val.useSpeedBasedMarkers ?? val.use_speed_based_markers ?? false);

            const geoData = {
                OfficeLatitude: lat,
                OfficeLongitude: lng,
                GeoRadiusMeters: rad,
                UseSpeedBasedMarkers: speed,
                Timestamp: new Date().toISOString()
            };

            if (window.payrollCompanySettings) {
                if (lat !== 0 && lng !== 0) {
                    window.payrollCompanySettings.officeLatitude = lat;
                    window.payrollCompanySettings.officeLongitude = lng;
                }
                if (rad > 0) {
                    window.payrollCompanySettings.geoRadiusMeters = rad;
                }
                window.payrollCompanySettings.useSpeedBasedMarkers = speed;
            }

            onGeoSettingsChanged(geoData, false);
        } catch (error) {
            console.warn('Firebase company settings callback failed:', error);
        }
    }

    const payrollBroadcastChannel = typeof window.BroadcastChannel === 'function'
        ? new BroadcastChannel('payroll_realtime_channel')
        : null;

    if (payrollBroadcastChannel) {
        payrollBroadcastChannel.onmessage = function (event) {
            if (!event || !event.data) return;
            const msg = event.data;
            if (msg.type === 'geo-settings-changed') {
                onGeoSettingsChanged(msg.data, true);
            }
        };
    }

    window.addEventListener('storage', function (e) {
        if (e.key === 'payroll_geo_settings_sync' && e.newValue) {
            try {
                const data = JSON.parse(e.newValue);
                onGeoSettingsChanged(data, true);
            } catch { }
        }
    });

    window.broadcastGeoSettingsChanged = function (data) {
        try {
            if (payrollBroadcastChannel) {
                payrollBroadcastChannel.postMessage({ type: 'geo-settings-changed', data: data });
            }
            localStorage.setItem('payroll_geo_settings_sync', JSON.stringify({ ...data, _ts: Date.now() }));
        } catch { }
        onGeoSettingsChanged(data, true);
    };

    function onGeoSettingsChanged(data, fromLocalBroadcast) {
        if (!fromLocalBroadcast) {
            try {
                if (payrollBroadcastChannel) {
                    payrollBroadcastChannel.postMessage({ type: 'geo-settings-changed', data: data });
                }
                localStorage.setItem('payroll_geo_settings_sync', JSON.stringify({ ...data, _ts: Date.now() }));
            } catch { }
        }
        notifyListeners('GeoSettingsChanged', data);
        window.dispatchEvent(new CustomEvent('geo-settings-changed', { detail: data }));
    }

    function onGeoPunchAuditChanged(data) {
        notifyListeners('GeoPunchAuditChanged', data);
        window.dispatchEvent(new CustomEvent('geo-punch-audit-changed', { detail: data }));
    }

    function onSessionInvalidated(employeeId, reason) {
        window.dispatchEvent(new CustomEvent('session-invalidated', { detail: { employeeId, reason } }));
    }

    function onDataChanged(data) { notifyListeners('AttendanceChanged', data); }
    function onAttendanceChanged(data) { notifyListeners('AttendanceChanged', data); }
    function onPunchChanged(data) { notifyListeners('AttendanceChanged', data); }
    function onLocationChanged(data) { notifyListeners('LocationChanged', data); }
    function onSessionStarted(data) { notifyListeners('SessionStarted', data); }
    function onSessionEnded(data) { notifyListeners('SessionEnded', data); }
    function onApplicationDataChanged(data) { notifyListeners('ApplicationDataChanged', data); }
    function onRegularizationChanged(data) { notifyListeners('RegularizationChanged', data); }
    function onLeaveChanged(data) { notifyListeners('LeaveChanged', data); }
    function onAdvanceChanged(data) { notifyListeners('AdvanceChanged', data); }
    function onBonusChanged(data) { notifyListeners('BonusChanged', data); }
    function onTaxDeclarationChanged(data) { notifyListeners('TaxDeclarationChanged', data); }
    function onEmployeeChanged(data) { notifyListeners('EmployeeChanged', data); }
    function onExitChanged(data) { notifyListeners('ExitChanged', data); }
    function onGlobalRefresh(data) { notifyListeners('AttendanceChanged', data); }

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

        for (const registration of currentListeners) {
            const listener = registration.ref;

            // An entity-filtered listener only receives events that actually
            // contain the requested entity. This prevents an Employee page
            // from reloading because of an unrelated payroll/attendance event.
            if (registration.entity && !applicationEventContainsEntity(data, registration.entity))
                continue;

            try {
                const targetMethod = typeof methodName === "string"
                    ? methodName
                    : "ApplicationDataChanged";

                await listener.invokeMethodAsync(targetMethod);
            }
            catch (error) {
                applicationListeners = applicationListeners.filter(function (item) {
                    return item.ref !== listener;
                });
            }
        }
    }

    function applicationEventContainsEntity(data, entity) {
        if (!data || !entity) return false;
        const changes = Array.isArray(data.changes) ? data.changes : [];
        const expected = String(entity).trim().toLowerCase();
        return changes.some(function (change) {
            const actual = String(change && (change.Entity || change.entity) || '')
                .trim().toLowerCase();
            return actual === expected;
        });
    }

    // Attendance Log Viewer depends on the finalized attendance projection
    // plus the inputs that can change the visible result (employee roster,
    // punches, leave, schedules, holidays, and regularization). Do not wake
    // the viewer for unrelated payroll/finance/admin events.
    const attendanceViewerEntities = new Set([
        'Employee',
        'AttendanceLog',
        'DailySummary',
        'LeaveRequest',
        'ShiftSchedule',
        'CompanyHoliday',
        'AttendanceRegularization'
    ]);

    function applicationEventAffectsAttendanceViewer(data) {
        if (!data || !Array.isArray(data.changes)) return false;

        return data.changes.some(function (change) {
            const entity = String(
                change && (change.Entity || change.entity) || ''
            ).trim();

            return attendanceViewerEntities.has(entity);
        });
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
                // is disposed and every future realtime event would fail again.
                //
                // IMPORTANT: Only remove the listener when the DotNetObjectReference
                // is actually disposed. Never remove a healthy listener simply because
                // that component does not implement an optional event method!
                const errMsg = String(error?.message || error || '');
                const isDisposed = errMsg.includes('disposed') || errMsg.includes('has already been disposed');

                if (isDisposed) {
                    const index = listeners.indexOf(listener);
                    if (index >= 0) {
                        listeners.splice(index, 1);
                    }
                } else if (typeof data !== "undefined") {
                    try {
                        await listener.invokeMethodAsync(methodName);
                    } catch (fallbackError) {
                        const fallbackMsg = String(fallbackError?.message || fallbackError || '');
                        if (fallbackMsg.includes('disposed') || fallbackMsg.includes('has already been disposed')) {
                            const index = listeners.indexOf(listener);
                            if (index >= 0) {
                                listeners.splice(index, 1);
                            }
                        }
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

    function registerApplication(dotNetReference, entityFilter) {

        applicationListeners = applicationListeners.filter(function (item) {
            return item.ref !== dotNetReference;
        });

        applicationListeners.push({
            ref: dotNetReference,
            entity: typeof entityFilter === "string" && entityFilter.trim()
                ? entityFilter.trim()
                : null
        });

        start();
    }

    async function unregisterApplication(dotNetReference) {

        applicationListeners =
            applicationListeners.filter(
                function (item) {
                    return item.ref !== dotNetReference;
                }
            );

        if (applicationListeners.length === 0 && applicationRefreshTimer !== null) {
            clearTimeout(applicationRefreshTimer);
            applicationRefreshTimer = null;
            applicationRefreshPending.clear();
        }
    }


    // Allow Blazor components to register for periodic LocationHealth bridge
    function registerLocationHealth(dotNetReference) {
        try {
            const handler = function (ev) {
                try {
                    const detail = ev.detail;
                    // A navigation/disposal can invalidate the reference while
                    // the browser event listener is still queued. Ignore that
                    // transient lifecycle condition.
                    dotNetReference.invokeMethodAsync('LocationChanged', null)
                        .catch(function () {
                            try {
                                unregisterLocationHealth(dotNetReference);
                            }
                            catch (cleanupError) { }
                        });
                }
                catch (e) { }
            };

            window.addEventListener('location-health-updated', handler);

            // store handler on the dotNetReference so unregister can remove
            dotNetReference._locationHealthHandler = handler;
        }
        catch (e) { }
    }

    function unregisterLocationHealth(dotNetReference) {
        try {
            if (dotNetReference && dotNetReference._locationHealthHandler) {
                window.removeEventListener('location-health-updated', dotNetReference._locationHealthHandler);
                dotNetReference._locationHealthHandler = null;
            }
        }
        catch (e) { }
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
            unregisterApplication,

        registerLocationHealth:
            registerLocationHealth,

        unregisterLocationHealth:
            unregisterLocationHealth

    };



})();