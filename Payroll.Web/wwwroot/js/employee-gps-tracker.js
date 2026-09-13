/**
 * ============================================================
 * PERSISTENT EMPLOYEE GPS TRACKER
 * ============================================================
 *
 * This module handles real-time GPS tracking for employees.
 *
 * RESPONSIBILITIES:
 * 1. Get browser geolocation (with permission)
 * 2. Watch location changes continuously
 * 3. Send location updates via HTTP API (independent of Blazor)
 * 4. Handle GPS errors gracefully
 * 5. Maintain session across browser tabs
 * 6. Support background tracking via Service Worker
 *
 * IMPORTANT:
 * - Each employee gets their own GPS session
 * - localStorage persists across tab close/reopen
 * - GPS watcher survives Blazor circuit reconnect
 * - GPS data sent via HTTP API, not dependent on Blazor JSInterop
 * - Continues tracking even when tab is inactive or closed
 */

window.EmployeeGpsTracker = (function () {

    // ============================================================
    // STATE
    // ============================================================

    let dotNetReference = null;
    let gpsWatcherId = null;
    let isWatching = false;
    let lastBroadcastTime = 0;
    let lastLocationData = null;
    let visibilityCheckInterval = null;
    let keepaliveInterval = null;
    let beforeUnloadHandler = null;
    let retryAttemptsMap = {};
    let employeeId = null;
    let gpsSessionId = null;
    let apiEndpoint = null;
    const BROADCAST_INTERVAL_MS = 5000; // Send updates every 5 seconds minimum
    const VISIBILITY_CHECK_INTERVAL_MS = 3000; // Check visibility every 3 seconds
    const FORCE_UPDATE_INTERVAL_MS = 10000; // Force GPS update every 10 seconds
    const KEEPALIVE_INTERVAL_MS = 60 * 1000; // Send keepalive every 60s to prevent session expiry
    const LOCATION_QUEUE_STORAGE_KEY = 'gps_location_queue';
    const EMPLOYEE_ID_STORAGE_KEY = 'current_employee_id';
    const GPS_SESSION_STORAGE_KEY = 'gps_session_id';
    const API_ENDPOINT_STORAGE_KEY = 'gps_api_endpoint';
    const IDB_DB_NAME = 'gps-tracker-db';
    const IDB_STORE_NAME = 'queuedLocations';

    // IndexedDB helpers
    function idbOpen() {
        return new Promise(function (resolve, reject) {
            try {
                const req = indexedDB.open(IDB_DB_NAME, 1);

                req.onupgradeneeded = function (ev) {
                    const db = ev.target.result;
                    if (!db.objectStoreNames.contains(IDB_STORE_NAME)) {
                        db.createObjectStore(IDB_STORE_NAME, { keyPath: 'id', autoIncrement: true });
                    }
                };

                req.onsuccess = function () { resolve(req.result); };
                req.onerror = function (e) { reject(e); };
            }
            catch (e) { reject(e); }
        });
    }

    function idbAddLocation(record) {
        return idbOpen().then(function (db) {
            return new Promise(function (resolve, reject) {
                try {
                    const tx = db.transaction(IDB_STORE_NAME, 'readwrite');
                    const store = tx.objectStore(IDB_STORE_NAME);
                    store.add(record);
                    tx.oncomplete = function () { resolve(true); };
                    tx.onerror = function (e) { reject(e); };
                }
                catch (e) { reject(e); }
            });
        });
    }

    function idbGetAllAndClear() {
        return idbOpen().then(function (db) {
            return new Promise(function (resolve, reject) {
                try {
                    const tx = db.transaction(IDB_STORE_NAME, 'readwrite');
                    const store = tx.objectStore(IDB_STORE_NAME);
                    const req = store.getAll();
                    req.onsuccess = function () {
                        const items = req.result || [];
                        const clearReq = store.clear();
                        clearReq.onsuccess = function () { resolve(items); };
                        clearReq.onerror = function (e) { reject(e); };
                    };
                    req.onerror = function (e) { reject(e); };
                }
                catch (e) { reject(e); }
            });
        });
    }

    // Keepalive control
    function startKeepalive() {
        try {
            if (keepaliveInterval !== null) return;
            const keepaliveUrl = '/api/employee-location/keepalive';
            keepaliveInterval = setInterval(function () {
                try {
                    const url = keepaliveUrl + '?_=' + Date.now();
                    fetch(url, {
                        method: 'GET',
                        credentials: 'include',
                        cache: 'no-store'
                    }).catch(function () { });
                }
                catch (e) { }
            }, KEEPALIVE_INTERVAL_MS);
            console.log('GPS keepalive started');
        }
        catch (e) {
            console.error('Failed to start keepalive:', e);
        }
    }

    function stopKeepalive() {
        try {
            if (keepaliveInterval !== null) {
                clearInterval(keepaliveInterval);
                keepaliveInterval = null;
                console.log('GPS keepalive stopped');
            }
        }
        catch (e) { }
    }

    // ============================================================
    // START PERSISTENT GPS WATCHER
    // ============================================================

    function startPersistentEmployeeGps(blazorReference, empId, sessionId, endpoint) {

        console.log('startPersistentEmployeeGps called with empId=' + empId);

        if (isWatching) {
            // The GPS watcher belongs to the browser page, not the Blazor
            // circuit. A reconnect/re-render may provide a new DotNetObjectReference.
            // Rebind it without stopping the existing watcher.
            dotNetReference = blazorReference || null;
            employeeId = empId;
            gpsSessionId = sessionId;
            apiEndpoint = endpoint || apiEndpoint || '/api/employee-location/update';

            try {
                localStorage.setItem(EMPLOYEE_ID_STORAGE_KEY, employeeId);
                localStorage.setItem(GPS_SESSION_STORAGE_KEY, gpsSessionId);
                localStorage.setItem(API_ENDPOINT_STORAGE_KEY, apiEndpoint);
            }
            catch (e) { }

            startKeepalive();
            console.log('GPS watcher already running. Blazor reference rebound.');
            return true;
        }

        dotNetReference = blazorReference;
        employeeId = empId;
        gpsSessionId = sessionId;
        apiEndpoint = endpoint || '/api/employee-location/update';

        // Store employee info in localStorage for background tracking
        try {
            localStorage.setItem(EMPLOYEE_ID_STORAGE_KEY, employeeId);
            localStorage.setItem(GPS_SESSION_STORAGE_KEY, gpsSessionId);
            localStorage.setItem(API_ENDPOINT_STORAGE_KEY, apiEndpoint);
            console.log('Stored GPS session info in localStorage');
        }
        catch (error) {
            console.error('Failed to store GPS session info:', error);
        }

        // Check if geolocation is supported
        if (!navigator.geolocation) {
            console.error('Geolocation is not supported by this browser');
            if (blazorReference && blazorReference.invokeMethodAsync) {
                blazorReference.invokeMethodAsync(
                    'PersistentEmployeeGpsError',
                    'Geolocation not supported');
            }
            return false;
        }

        try {
            // Start watching position with aggressive settings
            gpsWatcherId = navigator.geolocation.watchPosition(
                onLocationSuccess,
                onLocationError,
                {
                    enableHighAccuracy: true,
                    timeout: 10000,
                    maximumAge: 0  // Always get fresh location, never use cached
                }
            );

            isWatching = true;
            lastBroadcastTime = 0;
            lastLocationData = null;

            // Process any queued locations immediately when starting
            processQueuedLocations();

            // Start keepalive pings to prevent server session/cookie expiration
            startKeepalive();

            // Register a Service Worker (minimal) to enable Background Sync
            try {
                if ('serviceWorker' in navigator) {
                    navigator.serviceWorker.register('/service-worker.js')
                        .then(function (reg) {
                            console.log('ServiceWorker registered:', reg.scope);
                        })
                        .catch(function (err) {
                            console.warn('ServiceWorker registration failed:', err);
                        });
                }
            }
            catch (e) {
                // ignore
            }

            console.log('GPS watcher started. WatcherId=' + gpsWatcherId);

            // ============================================================
            // MONITOR TAB VISIBILITY
            // ============================================================
            // If tab becomes hidden, force GPS update when it becomes visible again
            // This keeps GPS active even when tab is in background
            // ============================================================
            
            if (document.addEventListener) {
                document.addEventListener('visibilitychange', onVisibilityChange);
                console.log('Visibility change listener registered');
            }

            // ============================================================
            // PERIODIC BACKGROUND CHECK
            // ============================================================
            // Force a location update periodically via HTTP API
            // This keeps admin dashboard updated even with inactive tab
            // ============================================================
            
            startVisibilityAndBackgroundCheck();

            // Attempt to flush queued locations when page is unloaded using sendBeacon
            if (window.addEventListener) {
                beforeUnloadHandler = function () {
                    try {
                        const queueJson = localStorage.getItem(LOCATION_QUEUE_STORAGE_KEY);
                        if (queueJson && navigator.sendBeacon && apiEndpoint) {
                            const blob = new Blob([queueJson], { type: 'application/json' });
                            // Try to send queued locations to apiEndpoint using sendBeacon
                            navigator.sendBeacon(apiEndpoint, blob);
                        }
                    }
                    catch (e) {
                        // ignore
                    }
                };
    // Listen for messages from service worker
    if (navigator.serviceWorker && navigator.serviceWorker.addEventListener) {
        navigator.serviceWorker.addEventListener('message', function (ev) {
            try {
                const data = ev.data;
                if (!data) return;

                if (data.type === 'PROCESS_GPS_QUEUE') {
                    processQueuedLocations();
                }
            }
            catch (e) {
                // ignore
            }
        });
    }

                window.addEventListener('beforeunload', beforeUnloadHandler);
            }

            return true;
        }
        catch (error) {
            console.error('Failed to start GPS watcher:', error);
            if (blazorReference && blazorReference.invokeMethodAsync) {
                blazorReference.invokeMethodAsync(
                    'PersistentEmployeeGpsError',
                    'Failed to start GPS: ' + error.message);
            }
            return false;
        }
    }

    // ============================================================
    // STOP GPS WATCHER
    // ============================================================

    function stopPersistentEmployeeGps() {

        console.log('stopPersistentEmployeeGps called');

        if (gpsWatcherId !== null && typeof gpsWatcherId !== 'undefined') {
            navigator.geolocation.clearWatch(gpsWatcherId);
            gpsWatcherId = null;
        }

        isWatching = false;
        dotNetReference = null;
        lastBroadcastTime = 0;
        lastLocationData = null;

        // Stop background checks
        stopVisibilityAndBackgroundCheck();

        // Stop keepalive pings
        stopKeepalive();

        // Remove event listeners
        if (document.removeEventListener) {
            document.removeEventListener('visibilitychange', onVisibilityChange);
        }
        if (beforeUnloadHandler && window.removeEventListener) {
            window.removeEventListener('beforeunload', beforeUnloadHandler);
            beforeUnloadHandler = null;
        }

        // A real logout must not leave old coordinates queued for a
        // future authenticated session.
        clearQueuedLocations();

        console.log('GPS watcher stopped');
    }

    // ============================================================
    // TAB VISIBILITY CHANGE HANDLER
    // ============================================================
    // When tab becomes visible, force a location update
    // This prevents stale status when tab is in background
    // ============================================================

    function onVisibilityChange() {

        if (!isWatching) {
            return;
        }

        if (document.hidden) {
            console.log('Tab is now HIDDEN - GPS will continue in background');
        }
        else {
            console.log('Tab is now VISIBLE - Forcing GPS update');
            // Restart the watcher to get fresh location
            forceLocationUpdate();
        }
    }

    // ============================================================
    // VISIBILITY AND BACKGROUND CHECK INTERVAL
    // ============================================================
    // Periodically force GPS update via HTTP API
    // This handles browser power management that pauses watchPosition
    // Works even if tab is inactive, circuit disconnected, or browser closed
    // ============================================================

    function startVisibilityAndBackgroundCheck() {

        if (visibilityCheckInterval !== null) {
            return;
        }

        visibilityCheckInterval = setInterval(function () {

            if (!isWatching) {
                stopVisibilityAndBackgroundCheck();
                return;
            }

            // Force a location update every 10 seconds
            // This keeps admin dashboard updated even if tab is inactive
            const timeSinceLastUpdate = Date.now() - lastBroadcastTime;

            if (timeSinceLastUpdate > FORCE_UPDATE_INTERVAL_MS) {
                console.log('Background force GPS update (' + FORCE_UPDATE_INTERVAL_MS + 'ms interval)');
                forceLocationUpdate();
            }

        }, VISIBILITY_CHECK_INTERVAL_MS);

        console.log('Background visibility/check interval started');
    }

    function stopVisibilityAndBackgroundCheck() {

        if (visibilityCheckInterval !== null) {
            clearInterval(visibilityCheckInterval);
            visibilityCheckInterval = null;
            console.log('Background visibility/check interval stopped');
        }
    }

    // ============================================================
    // FORCE LOCATION UPDATE
    // ============================================================
    // Request current position immediately
    // Bypasses the regular watchPosition interval
    // ============================================================

    function forceLocationUpdate() {

        if (!navigator.geolocation || !isWatching) {
            return;
        }

        try {
            navigator.geolocation.getCurrentPosition(
                function (position) {
                    console.log('Force update - getCurrentPosition success');
                    onLocationSuccess(position);
                },
                function (error) {
                    console.log('Force update - getCurrentPosition error: ' + error.message);
                    // Don't report error, just continue with watchPosition
                },
                {
                    enableHighAccuracy: true,
                    timeout: 5000,
                    maximumAge: 0
                }
            );
        }
        catch (error) {
            console.error('Force location update failed:', error);
        }
    }

    // ============================================================
    // LOCATION SUCCESS CALLBACK
    // ============================================================

    function onLocationSuccess(position) {

        if (!isWatching) {
            return;
        }

        try {
            const now = Date.now();
            const coords = position.coords;

            // --------------------------------------------------------
            // LOCAL MAP MOTION
            // --------------------------------------------------------
            // The browser may receive GPS fixes more frequently than the
            // server update throttle. Feed those real fixes directly to
            // the employee map so the marker remains visually continuous.
            // This does not change server/DB update frequency.
            try {
                if (window.updateEmployeeLiveGeoMap) {
                    window.updateEmployeeLiveGeoMap(
                        employeeId,
                        coords.latitude,
                        coords.longitude
                    );
                }
            }
            catch (mapError) {
                console.warn(
                    'Employee live map motion update failed:',
                    mapError
                );
            }

            // Store last location for forced updates
            lastLocationData = {
                latitude: coords.latitude,
                longitude: coords.longitude,
                accuracy: coords.accuracy,
                timestamp: now
            };

            // Throttle server broadcasts to the existing 5-second rate.
            // Only the visual interpolation above uses the more frequent
            // browser GPS callbacks.
            if (now - lastBroadcastTime < BROADCAST_INTERVAL_MS) {
                return;
            }

            lastBroadcastTime = now;

            console.log(
                'GPS Update: ' +
                'Lat=' + coords.latitude.toFixed(6) + ', ' +
                'Lon=' + coords.longitude.toFixed(6) + ', ' +
                'Accuracy=' + Math.round(coords.accuracy) + 'm'
            );

            const locationData = {
                latitude: coords.latitude,
                longitude: coords.longitude,
                accuracy: coords.accuracy
            };

            // IMPORTANT:
            //
            // GPS delivery is HTTP-first and independent of the Blazor
            // circuit. A disconnected/idle circuit must never stop location
            // delivery to the server.
            sendLocationViaHttpApi(locationData);
        }
        catch (error) {
            console.error('Error processing GPS location:', error);
        }
    }

    // ============================================================
    // SEND LOCATION VIA HTTP API
    // ============================================================
    // Send GPS data directly to API endpoint
    // Works even if Blazor circuit is disconnected
    // Queues updates if network is offline
    // ============================================================

    function sendLocationViaHttpApi(locationData) {

        if (!employeeId || !apiEndpoint) {
            console.warn('Cannot send GPS via HTTP: employeeId or apiEndpoint not set');
            return;
        }

        const payload = {
            employeeId: employeeId,
            sessionId: gpsSessionId,
            latitude: locationData.latitude,
            longitude: locationData.longitude,
            accuracy: locationData.accuracy,
            timestamp: new Date().toISOString()
        };

        const attemptSend = function (attempt) {
            fetch(apiEndpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(payload),
                credentials: 'include'
            })
            .then(response => {
                if (response.ok) {
                    console.log('GPS location sent via HTTP API');
                    // reset attempts
                    if (retryAttemptsMap && retryAttemptsMap[payload.timestamp]) {
                        delete retryAttemptsMap[payload.timestamp];
                    }
                }
                else {
                    console.warn('HTTP API returned status ' + response.status);
                    scheduleRetryOrQueue(locationData, attempt);
                }
            })
            .catch(error => {
                console.warn('Failed to send GPS via HTTP API, scheduling retry:', error);
                scheduleRetryOrQueue(locationData, attempt);
            });
        };

        attemptSend(0);
    }

    function scheduleRetryOrQueue(locationData, previousAttempt) {
        try {
            const key = (locationData && locationData.timestamp) ? locationData.timestamp : new Date().toISOString();
            const attempts = (retryAttemptsMap[key] || 0) + 1;
            retryAttemptsMap[key] = attempts;

            // Exponential backoff up to 6 attempts (~ up to 64s)
            if (attempts <= 6) {
                const delay = Math.pow(2, attempts) * 1000;
                setTimeout(function () {
                    sendLocationViaHttpApi(locationData);
                }, delay);
                return;
            }

            // If Background Sync is available, try to register a sync
            try {
                if (navigator.serviceWorker && navigator.serviceWorker.ready) {
                    navigator.serviceWorker.ready.then(function (reg) {
                        if (reg.sync) {
                            // save to queue first
                            queueLocationForRetry(locationData);
                            reg.sync.register('gps-location-sync').catch(function () {
                                // ignore
                            });
                        }
                        else {
                            queueLocationForRetry(locationData);
                        }
                    }).catch(function () {
                        queueLocationForRetry(locationData);
                    });
                }
                else {
                    queueLocationForRetry(locationData);
                }
            }
            catch (e) {
                queueLocationForRetry(locationData);
            }
        }
        catch (e) {
            queueLocationForRetry(locationData);
        }
    }

    // ============================================================
    // QUEUE LOCATION FOR RETRY
    // ============================================================
    // Store location data in localStorage when network is offline
    // Will be sent when network is restored
    // ============================================================

    function queueLocationForRetry(locationData) {
        try {
            const record = {
                employeeId: employeeId,
                sessionId: gpsSessionId,
                latitude: locationData.latitude,
                longitude: locationData.longitude,
                accuracy: locationData.accuracy,
                timestamp: new Date().toISOString()
            };

            idbAddLocation(record).then(function () {
                console.log('Location queued in IndexedDB for retry');
            }).catch(function (err) {
                console.error('Failed to queue location in IndexedDB, falling back to localStorage:', err);

                // Fallback to localStorage if IDB fails
                try {
                    let queue = [];
                    const queueJson = localStorage.getItem(LOCATION_QUEUE_STORAGE_KEY);
                    if (queueJson) queue = JSON.parse(queueJson);
                    queue.push(record);
                    if (queue.length > 100) queue = queue.slice(-100);
                    localStorage.setItem(LOCATION_QUEUE_STORAGE_KEY, JSON.stringify(queue));
                }
                catch (e) {
                    // ignore
                }
            });
        }
        catch (error) {
            console.error('Failed to queue location:', error);
        }
    }

    // ============================================================
    // PROCESS QUEUED LOCATIONS
    // ============================================================
    // Send any queued location data when network is back online
    // ============================================================

    function processQueuedLocations() {
        // Try IndexedDB first
        idbGetAllAndClear().then(function (items) {
            if (!items || items.length === 0) {
                // Fallback to localStorage
                try {
                    const queueJson = localStorage.getItem(LOCATION_QUEUE_STORAGE_KEY);
                    if (!queueJson) return;
                    const queue = JSON.parse(queueJson);
                    if (!queue || queue.length === 0) return;
                    console.log('Processing ' + queue.length + ' queued GPS locations (localStorage fallback)');
                    queue.forEach(function (locationData, index) {
                        setTimeout(function () { sendLocationViaHttpApi(locationData); }, index * 500);
                    });
                    localStorage.removeItem(LOCATION_QUEUE_STORAGE_KEY);
                }
                catch (e) {
                    // ignore
                }
                return;
            }

            console.log('Processing ' + items.length + ' queued GPS locations (IndexedDB)');

            items.forEach(function (locationData, index) {
                setTimeout(function () { sendLocationViaHttpApi(locationData); }, index * 500);
            });
        }).catch(function (err) {
            console.error('Failed to read IndexedDB queue, falling back to localStorage:', err);
            try {
                const queueJson = localStorage.getItem(LOCATION_QUEUE_STORAGE_KEY);
                if (!queueJson) return;
                const queue = JSON.parse(queueJson);
                if (!queue || queue.length === 0) return;
                console.log('Processing ' + queue.length + ' queued GPS locations (localStorage fallback)');
                queue.forEach(function (locationData, index) {
                    setTimeout(function () { sendLocationViaHttpApi(locationData); }, index * 500);
                });
                localStorage.removeItem(LOCATION_QUEUE_STORAGE_KEY);
            }
            catch (e) {
                // ignore
            }
        });
    }

    // ============================================================
    // LOCATION ERROR CALLBACK
    // ============================================================

    function onLocationError(error) {

        if (!isWatching) {
            return;
        }

        let errorMessage;

        switch (error.code) {
            case error.PERMISSION_DENIED:
                errorMessage = 'GPS permission denied. Enable location in browser settings.';
                break;
            case error.POSITION_UNAVAILABLE:
                errorMessage = 'GPS position unavailable. Retrying...';
                break;
            case error.TIMEOUT:
                errorMessage = 'GPS request timed out. Retrying...';
                break;
            default:
                errorMessage = 'GPS error: ' + error.message;
        }

        console.warn('GPS Error:', errorMessage);

        if (dotNetReference && dotNetReference.invokeMethodAsync) {
            dotNetReference.invokeMethodAsync(
                'PersistentEmployeeGpsError',
                errorMessage
            ).catch(err => {
                console.error('Failed to send GPS error to Blazor:', err);
            });
        }
    }

    // ============================================================
    // CREATE OR GET BROWSER SESSION ID
    // ============================================================

    function getOrCreateEmployeeGpsSessionId(storageKey, employeeId) {

        const prefixedKey = storageKey + '_' + employeeId;

        try {
            let sessionId = localStorage.getItem(prefixedKey);

            if (!sessionId || sessionId.trim() === '') {
                // Create new session ID
                sessionId = generateGuid();
                localStorage.setItem(prefixedKey, sessionId);
                console.log('Created new GPS session: ' + sessionId);
            }
            else {
                console.log('Retrieved existing GPS session: ' + sessionId);
            }

            return sessionId;
        }
        catch (error) {
            console.error('Failed to manage GPS session ID:', error);
            return generateGuid();
        }
    }

    // ============================================================
    // CREATE NEW SESSION ID (forced)
    // ============================================================

    function createNewEmployeeGpsSessionId(storageKey, employeeId) {

        const prefixedKey = storageKey + '_' + employeeId;

        try {
            const newSessionId = generateGuid();
            localStorage.setItem(prefixedKey, newSessionId);
            console.log('Created new forced GPS session: ' + newSessionId);
            return newSessionId;
        }
        catch (error) {
            console.error('Failed to create new GPS session ID:', error);
            return generateGuid();
        }
    }

    // ============================================================
    // CLEAR SESSION ID (on logout)
    // ============================================================

    function clearEmployeeGpsSessionId(storageKey, employeeId) {

        const prefixedKey = storageKey + '_' + employeeId;

        try {
            localStorage.removeItem(prefixedKey);
            console.log('Cleared GPS session ID');
        }
        catch (error) {
            console.error('Failed to clear GPS session ID:', error);
        }
    }

    // ============================================================
    // GENERATE GUID
    // ============================================================

    function generateGuid() {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            const r = Math.random() * 16 | 0;
            const v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }


    // ============================================================
    // REBIND AFTER BLAZOR CIRCUIT RECONNECT
    // ============================================================

    function bindPersistentEmployeeGps(blazorReference, empId, sessionId, endpoint) {
        return startPersistentEmployeeGps(
            blazorReference,
            empId,
            sessionId,
            endpoint);
    }

    function detachPersistentEmployeeGpsReference() {
        // Do NOT stop geolocation. Only detach the circuit callback.
        dotNetReference = null;
    }

    function clearQueuedLocations() {
        try {
            idbOpen().then(function (db) {
                try {
                    const tx = db.transaction(IDB_STORE_NAME, 'readwrite');
                    tx.objectStore(IDB_STORE_NAME).clear();
                }
                catch (e) { }
            }).catch(function () { });
        }
        catch (e) { }

        try {
            localStorage.removeItem(LOCATION_QUEUE_STORAGE_KEY);
        }
        catch (e) { }
    }

    // ============================================================
    // PUBLIC API
    // ============================================================

    return {
        startPersistentEmployeeGps: startPersistentEmployeeGps,
        bindPersistentEmployeeGps: bindPersistentEmployeeGps,
        detachPersistentEmployeeGpsReference: detachPersistentEmployeeGpsReference,
        stopPersistentEmployeeGps: stopPersistentEmployeeGps,
        getOrCreateEmployeeGpsSessionId: getOrCreateEmployeeGpsSessionId,
        createNewEmployeeGpsSessionId: createNewEmployeeGpsSessionId,
        clearEmployeeGpsSessionId: clearEmployeeGpsSessionId,
        sendLocationViaHttpApi: sendLocationViaHttpApi,
        processQueuedLocations: processQueuedLocations,
        forceLocationUpdate: forceLocationUpdate,
        getLatestLocation: function (maxAgeMs) {
            if (!lastLocationData) {
                return null;
            }

            const age = Date.now() - Number(lastLocationData.timestamp || 0);
            const maxAge = Number(maxAgeMs) || 30000;

            if (age < 0 || age > maxAge) {
                return null;
            }

            return {
                Latitude: Number(lastLocationData.latitude),
                Longitude: Number(lastLocationData.longitude),
                Accuracy: Number(lastLocationData.accuracy || 0)
            };
        }
    };

})();

// ============================================================
// NETWORK EVENT LISTENERS
// ============================================================
// Listen for online/offline events to handle queued GPS data
// ============================================================

if (window.addEventListener) {
    window.addEventListener('online', function () {
        console.log('Network connection restored. Processing queued GPS locations...');
        window.EmployeeGpsTracker.processQueuedLocations();
        window.EmployeeGpsTracker.forceLocationUpdate();
    });

    window.addEventListener('pageshow', function () {
        // Browser restored the page from bfcache/sleep. Reuse the existing
        // watcher and immediately request a fresh position.
        window.EmployeeGpsTracker.forceLocationUpdate();
    });

    window.addEventListener('offline', function () {
        console.log('Network connection lost. GPS locations will be queued.');
    });
}

// ============================================================
// EXPOSE FUNCTIONS TO GLOBAL SCOPE
// ============================================================
// These are called from Blazor components via JSRuntime

window.startPersistentEmployeeGps =
    function (blazorReference, employeeId, sessionId, apiEndpoint) {
        return window.EmployeeGpsTracker.startPersistentEmployeeGps(
            blazorReference,
            employeeId,
            sessionId,
            apiEndpoint);
    };

window.bindPersistentEmployeeGps =
    function (blazorReference, employeeId, sessionId, apiEndpoint) {
        return window.EmployeeGpsTracker.bindPersistentEmployeeGps(
            blazorReference,
            employeeId,
            sessionId,
            apiEndpoint);
    };

window.detachPersistentEmployeeGpsReference =
    function () {
        window.EmployeeGpsTracker.detachPersistentEmployeeGpsReference();
    };

window.stopPersistentEmployeeGps =
    function () {
        window.EmployeeGpsTracker.stopPersistentEmployeeGps();
    };

window.getOrCreateEmployeeGpsSessionId =
    function (storageKey, employeeId) {
        return window.EmployeeGpsTracker.getOrCreateEmployeeGpsSessionId(storageKey, employeeId);
    };

window.createNewEmployeeGpsSessionId =
    function (storageKey, employeeId) {
        return window.EmployeeGpsTracker.createNewEmployeeGpsSessionId(storageKey, employeeId);
    };

window.clearEmployeeGpsSessionId =
    function (storageKey, employeeId) {
        window.EmployeeGpsTracker.clearEmployeeGpsSessionId(storageKey, employeeId);
    };

window.sendLocationViaHttpApi =
    function (locationData) {
        window.EmployeeGpsTracker.sendLocationViaHttpApi(locationData);
    };

window.processQueuedLocations =
    function () {
        window.EmployeeGpsTracker.processQueuedLocations();
    };

// Returns the newest GPS fix already received by the persistent watcher.
// This lets dashboard widgets reuse the existing watcher instead of
// starting a second competing getCurrentPosition request.
window.getPersistentEmployeeGpsLocation =
    function (maxAgeMs) {
        return window.EmployeeGpsTracker.getLatestLocation(maxAgeMs || 30000);
    };

console.log('Employee GPS Tracker module loaded');
