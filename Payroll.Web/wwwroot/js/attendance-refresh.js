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

    // Firebase is an independent realtime transport. SignalR remains the
    // existing compatibility path, but Firebase keeps live GPS and CRUD
    // invalidation flowing when the Payroll.Web process is temporarily absent.
    let firebaseStarted = false;
    let firebaseStarting = false;
    let firebaseDatabase = null;
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
                    apiKey: 'AIzaSyDxIsBW8bq31gG7LqOm8-lwhmRFMsRu5CE',
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

            const liveRef = firebaseDatabase.ref('tracking/live');
            liveRef.on('child_added', onFirebaseLiveLocation);
            liveRef.on('child_changed', onFirebaseLiveLocation);

            const ownerUid = authResult.ownerUid || authResult.ownerUID || null;
            if (ownerUid) {
                const ownerEventsRef = firebaseDatabase.ref('owner_events/' + ownerUid);
                ownerEventsRef.on('child_added', onFirebaseApplicationEvent);

                // Geo-punch audits are listened to directly from Firebase.
                // This avoids waiting for the SQLite compatibility synchronizer
                // before an already-open Admin audit card can see a new record.
                const geoPunchAuditRef =
                    firebaseDatabase.ref('owners/' + ownerUid + '/geo_punch_audits');

                geoPunchAuditRef.on('child_added', onFirebaseGeoPunchAudit);
                geoPunchAuditRef.on('child_changed', onFirebaseGeoPunchAudit);

                // Mobile-originated changes use a per-employee channel so an
                // employee cannot write to the shared admin event stream.
                // Admin/SuperAdmin Firebase rules allow the web dashboard to
                // receive these events without changing the existing UI.
                const clientEventsRef = firebaseDatabase.ref('client_events');
                clientEventsRef.on('child_added', onFirebaseClientEventEmployee);
            } else {
                // Backward compatibility for installations that have not yet
                // configured a shared Firebase owner UID.
                const eventsRef = firebaseDatabase.ref('application_events');
                eventsRef.on('child_added', onFirebaseApplicationEvent);
            }

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

    async function onFirebaseLiveLocation(snapshot) {
        try {
            const data = snapshot.val();
            if (!data || !data.EmployeeId) return;

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
            if (!data || typeof data !== 'object')
                return;

            // Expose the Firebase record to the component-level realtime
            // listener. The component decides whether the selected employee
            // and selected date should be updated.
            window.dispatchEvent(
                new CustomEvent(
                    'firebase-geo-punch-audit-changed',
                    { detail: data }
                )
            );

            await notifyListeners(
                'GeoPunchAuditChanged',
                data
            );
        }
        catch (error) {
            console.warn(
                'Firebase geo punch audit callback failed.',
                error
            );
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

            await notifyApplicationListeners('ApplicationDataChanged', data);
            // Page-level listeners already expose ApplicationDataChanged and
            // own their existing data loaders. This updates the visible screen
            // without Navigation.Refresh/browser reload.
            await notifyListeners('ApplicationDataChanged', data);
            window.dispatchEvent(new CustomEvent('application-data-changed', { detail: data }));
        } catch (error) {
            console.warn('Firebase application event callback failed.', error);
        }
    }

    async function start() {
        // Firebase is the only realtime transport for the SSOT path.
        // Once this page has authenticated with Firebase, it does not need
        // SignalR/Render for subsequent realtime events.
        if (!firebaseStarted)
            await startFirebaseRealtime();
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

                // Application-wide listeners currently consume the event as an
                // invalidation signal. Do not marshal the arbitrary SignalR
                // payload into Blazor JS interop. This also prevents disposed
                // circuit references from producing parameter-registration
                // errors in the browser console.
                await listener.invokeMethodAsync(
                    targetMethod
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