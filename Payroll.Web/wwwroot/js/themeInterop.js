// ============================================================
// Payroll.Web - Shared Theme / Browser Interop
// ============================================================

window.payrollEscapeHtml = function (value) {
    return String(value ?? '').replace(/[&<>"']/g, function (ch) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[ch];
    });
};

// ============================================================
// THEME
// ============================================================

window.themeInterop = {
    setThemeOnBody: function (theme) {
        if (theme === 'dark') document.body.classList.add('dark');
        else document.body.classList.remove('dark');
    },
    saveTheme: function (theme) {
        try { localStorage.setItem('payroll_theme', theme); }
        catch (e) { console.warn('Unable to save theme to localStorage', e); }
    },
    loadTheme: function () {
        try { return localStorage.getItem('payroll_theme') || 'light'; }
        catch (e) { console.warn('Unable to read theme from localStorage', e); return 'light'; }
    },
    applySavedTheme: function () {
        var theme = this.loadTheme();
        this.setThemeOnBody(theme);
        return theme;
    }
};

// ============================================================
// GEOLOCATION - ROBUST CURRENT POSITION
// ============================================================

window.getCoords = async function () {

    if (!navigator.geolocation) {
        throw new Error(
            "Geolocation is not supported by this browser."
        );
    }

    function getPosition(options) {
        return new Promise(function (resolve, reject) {
            navigator.geolocation.getCurrentPosition(
                resolve,
                reject,
                options
            );
        });
    }

    function convertError(error) {

        switch (error.code) {

            case error.PERMISSION_DENIED:
                return new Error(
                    "Location permission was denied. Please allow location access."
                );

            case error.POSITION_UNAVAILABLE:
                return new Error(
                    "Location services are currently unavailable. Please turn on device Location."
                );

            case error.TIMEOUT:
                return new Error(
                    "GPS is taking too long to respond."
                );

            default:
                return new Error(
                    "Unable to determine your current location."
                );
        }
    }

    /*
     * First try a recent location. This is fast when the device
     * already has a recent GPS/network location.
     */
    try {

        const position = await getPosition({
            enableHighAccuracy: false,
            timeout: 8000,
            maximumAge: 15000
        });

        return {
            Latitude: position.coords.latitude,
            Longitude: position.coords.longitude,
            Accuracy: Number(position.coords.accuracy || 0)
        };

    } catch (firstError) {

        console.warn(
            "Normal GPS attempt failed:",
            firstError
        );
    }

    /*
     * Then request a fresh high-accuracy position.
     */
    try {

        const position = await getPosition({
            enableHighAccuracy: true,
            timeout: 15000,
            maximumAge: 0
        });

        return {
            Latitude: position.coords.latitude,
            Longitude: position.coords.longitude,
            Accuracy: Number(position.coords.accuracy || 0)
        };

    } catch (secondError) {

        console.warn(
            "High accuracy GPS attempt failed:",
            secondError
        );

        throw convertError(secondError);
    }
};

// ============================================================
// GPS ACCURACY HELPERS
// ============================================================

window.isUsableGpsAccuracy = function (accuracy, maximumMeters) {
    const value = Number(accuracy);
    const max = Number(maximumMeters) || 150;

    return Number.isFinite(value) &&
        value >= 0 &&
        value <= max;
};

window.formatGpsAccuracy = function (accuracy) {
    const value = Number(accuracy);

    if (!Number.isFinite(value) || value <= 0) {
        return '-';
    }

    return Math.round(value) + ' m';
};

// ============================================================
// LIVE MOBILE PUNCH LOCATION TRACKING
// ============================================================

window.mobilePunchLocationWatch = {

    watchId: null,

    dotNetReference: null,

    lastLatitude: null,

    lastLongitude: null,

    lastCallbackTime: 0,

    minimumMovementMeters: 3,

    maximumUpdateIntervalMs: 10000,


    start: function (dotNetReference) {

        this.stop();


        if (!navigator.geolocation) {

            console.warn(
                "Geolocation is not supported by this browser."
            );

            return false;
        }


        this.dotNetReference =
            dotNetReference;

        this.lastLatitude = null;

        this.lastLongitude = null;

        this.lastCallbackTime = 0;


        const self = this;


        this.watchId =
            navigator.geolocation.watchPosition(

                function (position) {

                    const latitude =
                        position.coords.latitude;

                    const longitude =
                        position.coords.longitude;

                    const now =
                        Date.now();


                    let shouldUpdate =
                        self.lastLatitude === null ||
                        self.lastLongitude === null;


                    if (!shouldUpdate) {

                        const movement =
                            self.calculateDistanceMeters(
                                self.lastLatitude,
                                self.lastLongitude,
                                latitude,
                                longitude
                            );

                        const elapsed =
                            now -
                            self.lastCallbackTime;


                        shouldUpdate =
                            movement >=
                            self.minimumMovementMeters ||
                            elapsed >=
                            self.maximumUpdateIntervalMs;
                    }


                    if (!shouldUpdate) {
                        return;
                    }


                    self.lastLatitude =
                        latitude;

                    self.lastLongitude =
                        longitude;

                    self.lastCallbackTime =
                        now;


                    if (self.dotNetReference) {

                        self.dotNetReference
                            .invokeMethodAsync(
                                "UpdateLiveLocation",
                                {
                                    Latitude:
                                        latitude,

                                    Longitude:
                                        longitude,

                                    Accuracy:
                                        Number(
                                            position.coords.accuracy || 0
                                        )
                                }
                            )
                            .catch(function (error) {

                                console.warn(
                                    "Live location callback failed:",
                                    error
                                );

                            });
                    }

                },

                function (error) {

                    /*
                     * A watch timeout is not necessarily
                     * a dead tracking session.
                     *
                     * Do not kill the Blazor live state here.
                     */

                    let message =
                        "Unable to track your location.";

                    switch (error.code) {

                        case error.PERMISSION_DENIED:

                            message =
                                "Location permission was denied.";

                            break;

                        case error.POSITION_UNAVAILABLE:

                            message =
                                "Your location is temporarily unavailable.";

                            break;

                        case error.TIMEOUT:

                            message =
                                "GPS temporarily timed out. Retrying...";

                            break;
                    }


                    console.warn(
                        "GPS watch:",
                        message
                    );


                    if (self.dotNetReference) {

                        self.dotNetReference
                            .invokeMethodAsync(
                                "LocationWatchError",
                                message
                            )
                            .catch(function (callbackError) {

                                console.warn(
                                    "Location error callback failed:",
                                    callbackError
                                );

                            });
                    }

                },

                {
                    enableHighAccuracy: true,

                    /*
                     * Increased from 10 seconds.
                     */
                    timeout: 30000,

                    /*
                     * Allow a recent position.
                     */
                    maximumAge: 10000
                }
            );


        return true;
    },


    stop: function () {

        if (this.watchId !== null) {

            try {

                navigator.geolocation.clearWatch(
                    this.watchId
                );

            }
            catch (e) {

                console.warn(
                    "Unable to stop location watcher:",
                    e
                );

            }
        }


        this.watchId = null;

        this.dotNetReference = null;

        this.lastLatitude = null;

        this.lastLongitude = null;

        this.lastCallbackTime = 0;
    },


    calculateDistanceMeters: function (
        lat1,
        lon1,
        lat2,
        lon2
    ) {

        const earthRadius =
            6371000;

        const dLat =
            (lat2 - lat1) *
            Math.PI / 180;

        const dLon =
            (lon2 - lon1) *
            Math.PI / 180;

        const rLat1 =
            lat1 *
            Math.PI / 180;

        const rLat2 =
            lat2 *
            Math.PI / 180;

        const a =
            Math.sin(dLat / 2) *
            Math.sin(dLat / 2) +
            Math.cos(rLat1) *
            Math.cos(rLat2) *
            Math.sin(dLon / 2) *
            Math.sin(dLon / 2);

        const c =
            2 *
            Math.atan2(
                Math.sqrt(a),
                Math.sqrt(1 - a)
            );

        return earthRadius * c;
    }
};


window.startMobilePunchLocationWatch =
    function (dotNetReference) {

        if (!window.mobilePunchLocationWatch) {

            console.error(
                "mobilePunchLocationWatch is not initialized."
            );

            return false;
        }

        return window.mobilePunchLocationWatch.start(
            dotNetReference
        );
    };


window.stopMobilePunchLocationWatch =
    function () {

        if (window.mobilePunchLocationWatch) {

            window.mobilePunchLocationWatch.stop();

        }
    };

// ============================================================
// PERSISTENT EMPLOYEE GPS TRACKING
// ============================================================
//
// IMPORTANT:
//
// This watcher belongs to EmployeeLayout / EmployeeGpsTracker,
// NOT MobilePunchWidget.
//
// Therefore navigating:
//
// Home
// Attendance
// Payslips
// Leave
// Advances
// Bonuses
//
// does NOT stop GPS.
//
// Only explicit logout / employee portal destruction stops it.
// ============================================================

window.persistentEmployeeGps = {

    watchId: null,

    dotNetReference: null,

    lastLatitude: null,

    lastLongitude: null,

    lastCallbackTime: 0,

    minimumMovementMeters: 3,

    maximumUpdateIntervalMs: 10000,

    start: function (dotNetReference) {

        /*
         * If already running, don't create another watcher.
         */
        if (this.watchId !== null) {

            this.dotNetReference =
                dotNetReference;

            return true;
        }

        if (!navigator.geolocation) {

            console.warn(
                "Persistent employee GPS is not supported."
            );

            return false;
        }

        this.dotNetReference =
            dotNetReference;

        this.lastLatitude = null;

        this.lastLongitude = null;

        this.lastCallbackTime = 0;

        const self = this;

        this.watchId =
            navigator.geolocation.watchPosition(

                function (position) {

                    const latitude =
                        Number(position.coords.latitude);

                    const longitude =
                        Number(position.coords.longitude);

                    const accuracy =
                        Number(
                            position.coords.accuracy || 0
                        );

                    const now =
                        Date.now();

                    if (
                        !Number.isFinite(latitude) ||
                        !Number.isFinite(longitude)
                    ) {
                        return;
                    }

                    let shouldUpdate =
                        self.lastLatitude === null ||
                        self.lastLongitude === null;

                    if (!shouldUpdate) {

                        const movement =
                            self.calculateDistanceMeters(
                                self.lastLatitude,
                                self.lastLongitude,
                                latitude,
                                longitude
                            );

                        const elapsed =
                            now -
                            self.lastCallbackTime;

                        shouldUpdate =
                            movement >=
                            self.minimumMovementMeters ||
                            elapsed >=
                            self.maximumUpdateIntervalMs;
                    }

                    if (!shouldUpdate) {
                        return;
                    }

                    self.lastLatitude =
                        latitude;

                    self.lastLongitude =
                        longitude;

                    self.lastCallbackTime =
                        now;

                    if (!self.dotNetReference) {
                        return;
                    }

                    self.dotNetReference
                        .invokeMethodAsync(
                            "UpdatePersistentEmployeeLocation",
                            {
                                Latitude: latitude,
                                Longitude: longitude,
                                Accuracy: accuracy
                            }
                        )
                        .catch(function (error) {

                            /*
                             * Do NOT stop the browser watcher
                             * just because a Blazor callback
                             * temporarily failed.
                             */

                            console.warn(
                                "Persistent GPS callback failed:",
                                error
                            );
                        });
                },

                function (error) {

                    let message =
                        "Unable to track your location.";

                    switch (error.code) {

                        case error.PERMISSION_DENIED:

                            message =
                                "Location permission was denied.";

                            break;

                        case error.POSITION_UNAVAILABLE:

                            message =
                                "Your location is temporarily unavailable.";

                            break;

                        case error.TIMEOUT:

                            message =
                                "GPS temporarily timed out. Retrying...";

                            break;
                    }

                    console.warn(
                        "Persistent employee GPS:",
                        message
                    );

                    /*
                     * IMPORTANT:
                     *
                     * Do not clear the last server location.
                     *
                     * Admin will automatically transition:
                     *
                     * LIVE → STALE → OFFLINE
                     */
                    if (self.dotNetReference) {

                        self.dotNetReference
                            .invokeMethodAsync(
                                "PersistentEmployeeGpsError",
                                message
                            )
                            .catch(function (callbackError) {

                                console.warn(
                                    "Persistent GPS error callback failed:",
                                    callbackError
                                );

                            });
                    }
                },

                {
                    enableHighAccuracy: true,

                    timeout: 30000,

                    maximumAge: 10000
                }
            );

        return true;
    },

    stop: function () {

        if (this.watchId !== null) {

            try {

                navigator.geolocation.clearWatch(
                    this.watchId
                );

            }
            catch (e) {

                console.warn(
                    "Unable to stop persistent employee GPS:",
                    e
                );
            }
        }

        this.watchId = null;

        this.dotNetReference = null;

        this.lastLatitude = null;

        this.lastLongitude = null;

        this.lastCallbackTime = 0;
    },

    calculateDistanceMeters: function (
        lat1,
        lon1,
        lat2,
        lon2
    ) {

        const earthRadius =
            6371000;

        const dLat =
            (lat2 - lat1) *
            Math.PI / 180;

        const dLon =
            (lon2 - lon1) *
            Math.PI / 180;

        const rLat1 =
            lat1 *
            Math.PI / 180;

        const rLat2 =
            lat2 *
            Math.PI / 180;

        const a =
            Math.sin(dLat / 2) *
            Math.sin(dLat / 2) +
            Math.cos(rLat1) *
            Math.cos(rLat2) *
            Math.sin(dLon / 2) *
            Math.sin(dLon / 2);

        const c =
            2 *
            Math.atan2(
                Math.sqrt(a),
                Math.sqrt(1 - a)
            );

        return earthRadius * c;
    }
};


// ============================================================
// START PERSISTENT EMPLOYEE GPS
// ============================================================

window.startPersistentEmployeeGps =
    function (dotNetReference) {

        if (!window.persistentEmployeeGps) {

            console.error(
                "Persistent employee GPS is not initialized."
            );

            return false;
        }

        return window.persistentEmployeeGps.start(
            dotNetReference
        );
    };

// ============================================================
// EMPLOYEE GPS BROWSER SESSION ID
// ============================================================
//
// IMPORTANT:
//
// sessionStorage is destroyed when a browser tab is closed.
//
// localStorage is used here because we want:
//
//     Login
//       ↓
//     GPS Session A
//       ↓
//     Close tab
//       ↓
//     Open tab again
//       ↓
//     GPS Session A
//
// The key is employee-specific.
//
// The value is cleared ONLY during real logout.
// ============================================================

window.getOrCreateEmployeeGpsSessionId =
    function (storageKey, employeeId) {

        const baseKey =
            storageKey ||
            "payroll_employee_gps_session_id";

        const employeeKey =
            Number(employeeId) > 0
                ? baseKey + "_" + Number(employeeId)
                : baseKey;

        try {

            let existing =
                window.localStorage.getItem(
                    employeeKey
                );

            if (existing) {

                const parsed =
                    String(existing).trim();

                /*
                 * Validate GUID.
                 */
                if (
                    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
                        .test(parsed)
                ) {
                    return parsed;
                }

                /*
                 * Invalid value.
                 */
                window.localStorage.removeItem(
                    employeeKey
                );
            }

            /*
             * Generate UUID v4.
             */
            const bytes =
                new Uint8Array(16);

            if (
                window.crypto &&
                window.crypto.getRandomValues
            ) {
                window.crypto.getRandomValues(
                    bytes
                );
            }
            else {
                for (
                    let i = 0;
                    i < bytes.length;
                    i++
                ) {
                    bytes[i] =
                        Math.floor(
                            Math.random() * 256
                        );
                }
            }

            /*
             * RFC 4122 UUID v4.
             */
            bytes[6] =
                (bytes[6] & 0x0f) | 0x40;

            bytes[8] =
                (bytes[8] & 0x3f) | 0x80;

            const hex =
                Array.from(
                    bytes,
                    function (b) {
                        return b
                            .toString(16)
                            .padStart(2, "0");
                    }
                );

            const id =
                hex.slice(0, 4).join("") + "-" +
                hex.slice(4, 6).join("") + "-" +
                hex.slice(6, 8).join("") + "-" +
                hex.slice(8, 10).join("") + "-" +
                hex.slice(10, 16).join("");

            window.localStorage.setItem(
                employeeKey,
                id
            );

            return id;
        }
        catch (error) {

            console.warn(
                "Unable to use localStorage for employee GPS session:",
                error
            );

            /*
             * Last-resort in-memory fallback.
             */
            const fallbackKey =
                "__payrollFallbackGpsSessionId_" +
                String(employeeId || "unknown");

            if (
                window[fallbackKey]
            ) {
                return window[fallbackKey];
            }

            const fallbackBytes =
                new Uint8Array(16);

            if (
                window.crypto &&
                window.crypto.getRandomValues
            ) {
                window.crypto.getRandomValues(
                    fallbackBytes
                );
            }
            else {
                for (
                    let i = 0;
                    i < fallbackBytes.length;
                    i++
                ) {
                    fallbackBytes[i] =
                        Math.floor(
                            Math.random() * 256
                        );
                }
            }

            fallbackBytes[6] =
                (fallbackBytes[6] & 0x0f) | 0x40;

            fallbackBytes[8] =
                (fallbackBytes[8] & 0x3f) | 0x80;

            const fallbackHex =
                Array.from(
                    fallbackBytes,
                    function (b) {
                        return b
                            .toString(16)
                            .padStart(2, "0");
                    }
                );

            const fallback =
                fallbackHex.slice(0, 4).join("") + "-" +
                fallbackHex.slice(4, 6).join("") + "-" +
                fallbackHex.slice(6, 8).join("") + "-" +
                fallbackHex.slice(8, 10).join("") + "-" +
                fallbackHex.slice(10, 16).join("");

            window[fallbackKey] =
                fallback;

            return fallback;
        }
    };


// ============================================================
// CREATE COMPLETELY NEW GPS SESSION
// ============================================================
//
// Used when the previously stored SessionId has already ended.
//
// Example:
//
// Session A
//     ↓
// TIMED_OUT
//     ↓
// Employee opens portal
//     ↓
// Remove Session A
//     ↓
// Create Session B
// ============================================================

window.createNewEmployeeGpsSessionId =
    function (storageKey, employeeId) {

        const baseKey =
            storageKey ||
            "payroll_employee_gps_session_id";

        const employeeKey =
            Number(employeeId) > 0
                ? baseKey + "_" + Number(employeeId)
                : baseKey;

        try {

            window.localStorage.removeItem(
                employeeKey
            );

        }
        catch (error) {

            console.warn(
                "Unable to remove old employee GPS session:",
                error
            );
        }

        /*
         * Also remove fallback.
         */
        try {

            delete window[
                "__payrollFallbackGpsSessionId_" +
                String(employeeId || "unknown")
            ];

        }
        catch {
        }

        return window.getOrCreateEmployeeGpsSessionId(
            baseKey,
            employeeId
        );
    };


// ============================================================
// CLEAR EMPLOYEE GPS SESSION
// ============================================================
//
// ONLY call this during REAL LOGOUT.
//
// Do NOT call this from:
//     DisposeAsync()
//     circuit disconnect
//     network failure
//     GPS failure
// ============================================================

window.clearEmployeeGpsSessionId =
    function (storageKey, employeeId) {

        const baseKey =
            storageKey ||
            "payroll_employee_gps_session_id";

        const employeeKey =
            Number(employeeId) > 0
                ? baseKey + "_" + Number(employeeId)
                : baseKey;

        try {

            window.localStorage.removeItem(
                employeeKey
            );

        }
        catch (error) {

            console.warn(
                "Unable to clear employee GPS session ID:",
                error
            );
        }

        try {

            delete window[
                "__payrollFallbackGpsSessionId_" +
                String(employeeId || "unknown")
            ];

        }
        catch {
        }
    };

window.submitEmployeeLogoutForm = function (formId) {

    const form = document.getElementById(formId);

    if (!form) {
        console.error(
            "Logout form not found:",
            formId
        );
        return;
    }

    // ------------------------------------------------------------
    // REAL LOGOUT ONLY
    //
    // Clear persistent employee GPS session IDs.
    //
    // We intentionally do NOT clear these during:
    // - tab close
    // - page navigation
    // - circuit disconnect
    // - network interruption
    // - GPS failure
    // ------------------------------------------------------------

    try {

        const prefix =
            "payroll_employee_gps_session_id_";

        const keysToRemove = [];

        for (
            let i = 0;
            i < window.localStorage.length;
            i++
        ) {

            const key =
                window.localStorage.key(i);

            if (
                key &&
                key.startsWith(prefix)
            ) {
                keysToRemove.push(key);
            }
        }

        keysToRemove.forEach(
            function (key) {

                try {
                    window.localStorage.removeItem(key);
                }
                catch (error) {
                    console.warn(
                        "Unable to remove GPS session key:",
                        key,
                        error
                    );
                }

            }
        );

        // Also clear the old non-employee-specific key
        try {
            window.localStorage.removeItem(
                "payroll_employee_gps_session_id"
            );
        }
        catch {
        }

    }
    catch (error) {

        console.warn(
            "Unable to clear employee GPS browser sessions:",
            error
        );
    }

    // ------------------------------------------------------------
    // Finally submit the Identity logout form.
    // ------------------------------------------------------------

    form.submit();
};

// ============================================================
// STOP PERSISTENT EMPLOYEE GPS
// ============================================================

window.stopPersistentEmployeeGps =
    function () {

        if (
            window.persistentEmployeeGps
        ) {

            window.persistentEmployeeGps.stop();

        }
    };

// ============================================================
// GPS CLEANUP
// ============================================================

window.stopAllPayrollGps = function () {

    try {

        if (window.persistentEmployeeGps) {
            window.persistentEmployeeGps.stop();
        }

    }
    catch (e) {

        console.warn(
            "Unable to stop persistent employee GPS:",
            e
        );

    }

    try {

        if (window.mobilePunchLocationWatch) {
            window.mobilePunchLocationWatch.stop();
        }

    }
    catch (e) {

        console.warn(
            "Unable to stop mobile punch GPS:",
            e
        );

    }

    try {

        Object.keys(
            window.adminHistoryPlayback || {}
        ).forEach(
            function (mapId) {

                window.stopAdminHistoryPlayback(
                    mapId
                );

            }
        );

    }
    catch (e) {

        console.warn(
            "Unable to stop GPS playback:",
            e
        );

    }
};

// ============================================================
// PRINT
// ============================================================

window.PrintElement = function (elementId) {
    const elementToPrint =
        document.getElementById(elementId);

    if (!elementToPrint) {
        console.error(
            'Element to print not found:',
            elementId
        );
        return;
    }

    elementToPrint.classList.add(
        'printable-payslip'
    );

    window.print();

    setTimeout(function () {
        elementToPrint.classList.remove(
            'printable-payslip'
        );
    }, 500);
};

// ============================================================
// DOWNLOAD FILE
// ============================================================

window.downloadFileFromStream =
    async function (
        fileName,
        contentStreamReference
    ) {
        const arrayBuffer =
            await contentStreamReference.arrayBuffer();

        const blob =
            new Blob([arrayBuffer]);

        const url =
            URL.createObjectURL(blob);

        const anchorElement =
            document.createElement('a');

        anchorElement.href = url;
        anchorElement.download = fileName ?? '';

        document.body.appendChild(
            anchorElement
        );

        anchorElement.click();
        anchorElement.remove();

        URL.revokeObjectURL(url);
    };

// ============================================================
// BOOTSTRAP DROPDOWNS
// ============================================================

window.initBootstrapDropdowns =
    function () {
        var dropdownElementList =
            document.querySelectorAll(
                '[data-bs-toggle="dropdown"]'
            );

        if (
            typeof bootstrap === 'undefined' ||
            !bootstrap.Dropdown
        ) {
            return;
        }

        dropdownElementList.forEach(
            function (dropdownToggleEl) {
                bootstrap.Dropdown
                    .getOrCreateInstance(
                        dropdownToggleEl
                    );
            }
        );
    };

// ============================================================
// LEAFLET SHARED LOADER
// ============================================================

window.payrollLeafletPromise = null;

window.loadPayrollLeaflet =
    function () {
        if (window.L) {
            return Promise.resolve();
        }

        if (window.payrollLeafletPromise) {
            return window.payrollLeafletPromise;
        }

        window.payrollLeafletPromise =
            new Promise(
                function (resolve, reject) {
                    if (
                        !document.querySelector(
                            'link[data-payroll-leaflet]'
                        )
                    ) {
                        const css =
                            document.createElement(
                                'link'
                            );

                        css.rel = 'stylesheet';

                        css.href =
                            'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/leaflet.css';

                        css.dataset.payrollLeaflet =
                            '1';

                        document.head.appendChild(
                            css
                        );
                    }

                    const script =
                        document.createElement(
                            'script'
                        );

                    script.src =
                        'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/leaflet.js';

                    script.onload =
                        function () {
                            resolve();
                        };

                    script.onerror =
                        function () {
                            window.payrollLeafletPromise = null;
                            reject(
                                new Error(
                                    'Unable to load map library.'
                                )
                            );
                        };

                    document.head.appendChild(
                        script
                    );
                }
            );

        return window.payrollLeafletPromise;
    };

// ============================================================
// SMOOTH LIVE GPS MOVEMENT
// ============================================================
// The GPS/network layer intentionally reports real coordinates at a
// controlled rate.  These helpers only interpolate the marker between
// real GPS points in the browser.  They never invent a new GPS point or
// write anything to the database.
// ============================================================

window.payrollGeoAnimationState =
    window.payrollGeoAnimationState || {};

window.payrollSmoothMoveMarker =
    function (marker, key, target, durationMs, onFrame) {
        if (!marker || !Array.isArray(target) || target.length < 2) {
            return;
        }

        const stateStore = window.payrollGeoAnimationState;
        const previous = stateStore[key];

        if (previous && previous.frame) {
            try {
                cancelAnimationFrame(previous.frame);
            }
            catch { }
        }

        const startLatLng = marker.getLatLng();
        const start = [
            Number(startLatLng.lat),
            Number(startLatLng.lng)
        ];

        const end = [
            Number(target[0]),
            Number(target[1])
        ];

        if (
            !Number.isFinite(start[0]) ||
            !Number.isFinite(start[1]) ||
            !Number.isFinite(end[0]) ||
            !Number.isFinite(end[1])
        ) {
            marker.setLatLng(end);
            if (typeof onFrame === 'function') {
                onFrame(end);
            }
            return;
        }

        const deltaLat = end[0] - start[0];
        const deltaLng = end[1] - start[1];

        if (
            Math.abs(deltaLat) < 0.00000001 &&
            Math.abs(deltaLng) < 0.00000001
        ) {
            marker.setLatLng(end);
            if (typeof onFrame === 'function') {
                onFrame(end);
            }
            return;
        }

        const duration = Math.max(
            250,
            Math.min(
                6000,
                Number(durationMs) || 4000
            )
        );

        const startedAt = performance.now();
        const animation = {
            frame: 0
        };

        stateStore[key] = animation;

        function step(now) {
            const raw = Math.min(
                1,
                Math.max(
                    0,
                    (now - startedAt) / duration
                )
            );

            // Smooth but constant-looking travel between GPS fixes.
            const progress =
                raw < 0.5
                    ? 2 * raw * raw
                    : 1 - Math.pow(-2 * raw + 2, 2) / 2;

            const position = [
                start[0] + deltaLat * progress,
                start[1] + deltaLng * progress
            ];

            marker.setLatLng(position);

            if (typeof onFrame === 'function') {
                try {
                    onFrame(position);
                }
                catch { }
            }

            if (raw < 1) {
                animation.frame =
                    requestAnimationFrame(step);
            }
            else {
                marker.setLatLng(end);

                if (typeof onFrame === 'function') {
                    try {
                        onFrame(end);
                    }
                    catch { }
                }

                if (stateStore[key] === animation) {
                    delete stateStore[key];
                }
            }
        }

        animation.frame = requestAnimationFrame(step);
    };

window.payrollCancelGeoAnimation =
    function (key) {
        const state =
            window.payrollGeoAnimationState?.[key];

        if (state?.frame) {
            try {
                cancelAnimationFrame(state.frame);
            }
            catch { }
        }

        if (window.payrollGeoAnimationState) {
            delete window.payrollGeoAnimationState[key];
        }
    };

// ============================================================
// ROAD ROUTING + JOURNEY DETAILS
// ============================================================
// Presentation-only road routing. Existing GPS, attendance, session and
// database flows remain unchanged. The routing URL is replaceable for a
// production/self-hosted routing service.

window.payrollRoutingServiceUrl = window.payrollRoutingServiceUrl || 'https://router.project-osrm.org';
window.payrollJourneyState = window.payrollJourneyState || {};

window.payrollHaversineMeters = function (a, b) {
    if (!Array.isArray(a) || !Array.isArray(b)) return 0;
    const v = [Number(a[0]), Number(a[1]), Number(b[0]), Number(b[1])];
    if (!v.every(Number.isFinite)) return 0;
    const R = 6371000;
    const dLat = (v[2] - v[0]) * Math.PI / 180;
    const dLon = (v[3] - v[1]) * Math.PI / 180;
    const p1 = v[0] * Math.PI / 180;
    const p2 = v[2] * Math.PI / 180;
    const h = Math.sin(dLat / 2) ** 2 + Math.cos(p1) * Math.cos(p2) * Math.sin(dLon / 2) ** 2;
    return 2 * R * Math.asin(Math.min(1, Math.sqrt(h)));
};

window.payrollFormatRouteDistance = function (meters) {
    const m = Number(meters) || 0;
    return m < 1000 ? `${Math.round(m)} m` : `${(m / 1000).toFixed(1)} km`;
};

window.payrollFormatRouteDuration = function (seconds) {
    const s = Math.max(0, Math.round(Number(seconds) || 0));
    if (s < 60) return `${s}s`;
    const minutes = Math.round(s / 60);
    if (minutes < 60) return `${minutes} min`;
    const hours = Math.floor(minutes / 60);
    const mins = minutes % 60;
    return mins ? `${hours}h ${mins}m` : `${hours}h`;
};

window.payrollFormatSpeed = function (metersPerSecond) {
    const speed = Number(metersPerSecond);
    if (!Number.isFinite(speed) || speed <= 0.15) return 'Stopped';
    const kmh = speed * 3.6;
    if (kmh < 1) return 'Slow';
    return `${kmh.toFixed(1)} km/h`;
};

window.payrollCreateAdminTooltipHtml = function (data, initials, withinRange, distance, tooltipDistance, tooltipEta, tooltipSpeed) {
    const safeName = window.escapeAdminHtml(data.name || 'Employee');
    const statusText = withinRange ? 'Within range' : 'Outside range';
    const statusClass = withinRange ? 'within' : 'outside';

    return `<div class="admin-live-hover-card">` +
           `<div class="admin-live-hover-title">` +
           `<span class="hover-avatar">${initials}</span>` +
           `<strong>${safeName}</strong>` +
           `<span class="hover-state ${statusClass}">${statusText}</span>` +
           `</div>` +
           `<div class="admin-live-hover-grid">` +
           `<span><small>Air Distance</small><b>${distance}</b></span>` +
           `<span><small>Road Distance</small><b>${tooltipDistance}</b></span>` +
           `<span><small>ETA</small><b>${tooltipEta}</b></span>` +
           `<span><small>Speed</small><b>${tooltipSpeed}</b></span>` +
           `<span><small>Allowed Radius</small><b>${data.allowedRadiusMeters || 0} m</b></span>` +
           `<span><small>Accuracy</small><b>±${Math.round(data.accuracyMeters || 0)} m</b></span>` +
           `</div></div>`;
};

window.payrollFetchRoadRoute = async function (from, to, options = {}) {
    const a = [Number(from[0]), Number(from[1])];
    const b = [Number(to[0]), Number(to[1])];
    if (![...a, ...b].every(Number.isFinite)) return null;
    const base = String(window.payrollRoutingServiceUrl || '').replace(/\/$/, '');
    if (!base) return null;
    const url = `${base}/route/v1/driving/${a[1]},${a[0]};${b[1]},${b[0]}?overview=full&geometries=geojson&steps=true&annotations=false`;
    const response = await fetch(url, { method: 'GET', mode: 'cors', cache: 'no-store', signal: options.controller?.signal });
    if (!response.ok) throw new Error(`Routing service HTTP ${response.status}`);
    const data = await response.json();
    if (data.code !== 'Ok' || !data.routes?.[0]) return null;
    const route = data.routes[0];
    const geometry = (route.geometry?.coordinates || [])
        .filter(c => Array.isArray(c) && c.length >= 2)
        .map(c => [Number(c[1]), Number(c[0])])
        .filter(c => c.every(Number.isFinite));
    if (geometry.length < 2) return null;
    const steps = (route.legs || []).flatMap(leg => Array.isArray(leg.steps) ? leg.steps : [])
        .filter(step => step && (step.name || step.ref));
    return {
        distanceMeters: Number(route.distance) || 0,
        durationSeconds: Number(route.duration) || 0,
        geometry,
        steps,
        generatedAt: Date.now()
    };
};

window.payrollGetNextRoadName = function (route) {
    const step = (route?.steps || []).find(s => String(s.name || '').trim());
    if (!step) return 'Road route';
    const name = String(step.name || '').trim();
    const ref = String(step.ref || '').trim();
    return ref && ref !== name ? `${name} (${ref})` : name;
};

window.payrollCreateJourneyOverlay = function (mapElement, className) {
    if (!mapElement) return null;
    if (!document.getElementById('payroll-journey-map-global-style')) {
        const style = document.createElement('style');
        style.id = 'payroll-journey-map-global-style';
        style.textContent = `
.payroll-admin-journey-tooltip{background:transparent!important;border:0!important;box-shadow:none!important;padding:0!important;color:inherit!important}.payroll-admin-journey-tooltip:before{display:none!important}.payroll-admin-journey-label{min-width:178px;max-width:235px;padding:7px 8px;border-radius:13px;background:rgba(255,255,255,.96);border:1px solid rgba(22,136,255,.20);box-shadow:0 9px 24px rgba(15,31,55,.24),0 2px 8px rgba(15,31,55,.12);backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);color:#172238;font-size:9px;line-height:1.15}.payroll-admin-journey-head{display:flex;align-items:center;justify-content:space-between;gap:7px}.payroll-admin-journey-name{font-size:11px;font-weight:900;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.payroll-admin-journey-state{font-size:7px;font-weight:900;white-space:nowrap}.payroll-admin-journey-destination{margin-top:3px;color:#718096;font-size:7px;font-weight:800}.payroll-admin-journey-grid{display:grid;grid-template-columns:1fr 1fr;gap:3px;margin-top:5px}.payroll-admin-journey-grid span{display:block;padding:4px 4px;border-radius:7px;background:#f1f5fa;border:1px solid rgba(19,43,77,.07);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.payroll-admin-journey-grid b{font-weight:900}.payroll-admin-journey-tooltip .leaflet-tooltip-content{margin:0!important}[data-theme="dark"] .payroll-admin-journey-label,[data-bs-theme="dark"] .payroll-admin-journey-label{background:rgba(14,22,35,.96);border-color:rgba(79,166,255,.25);box-shadow:0 12px 28px rgba(0,0,0,.48);color:#edf5ff}.payroll-admin-journey-grid span,[data-theme="dark"] .payroll-admin-journey-grid span,[data-bs-theme="dark"] .payroll-admin-journey-grid span{color:#25354a}.payroll-admin-journey-grid span{color:#25354a}[data-theme="dark"] .payroll-admin-journey-grid span,[data-bs-theme="dark"] .payroll-admin-journey-grid span{background:rgba(29,43,61,.78);border-color:rgba(143,177,214,.12);color:#dbeaff}.payroll-admin-journey-destination{color:#718096}[data-theme="dark"] .payroll-admin-journey-destination,[data-bs-theme="dark"] .payroll-admin-journey-destination{color:#8fa4bb}@media(max-width:900px){.payroll-admin-journey-label{min-width:150px;max-width:190px;padding:6px 7px}.payroll-admin-journey-name{font-size:10px}.payroll-admin-journey-grid{gap:2px}.payroll-admin-journey-grid span{padding:3px;font-size:8px}}.admin-employee-label,.payroll-employee-name-label{background:rgba(10,18,30,.92)!important;color:#fff!important;border:1px solid rgba(255,255,255,.18)!important;border-radius:10px!important;box-shadow:0 5px 14px rgba(0,0,0,.25)!important;font-size:11px!important;font-weight:800!important;padding:4px 8px!important}.admin-distance-label{background:rgba(13,110,253,.94)!important;color:#fff!important;border:0!important;border-radius:9px!important;font-weight:800!important;padding:3px 7px!important}
.payroll-journey-overlay{position:absolute;left:10px;top:10px;z-index:1000;width:min(285px,calc(100% - 20px));min-width:0;max-width:calc(100% - 20px);padding:0!important;border-radius:16px!important;overflow:hidden;pointer-events:none;color:#162033;background:rgba(255,255,255,.94);border:1px solid rgba(19,43,77,.12);box-shadow:0 14px 34px rgba(15,31,55,.22),0 3px 10px rgba(15,31,55,.10);backdrop-filter:blur(18px) saturate(145%);-webkit-backdrop-filter:blur(18px) saturate(145%);font-size:10px;line-height:1.15}
.payroll-journey-card{padding:7px 8px 6px;background:linear-gradient(145deg,rgba(255,255,255,.98),rgba(244,248,253,.94));}.payroll-journey-top{display:flex;align-items:center;gap:7px;margin-bottom:5px;min-height:31px}.payroll-journey-avatar{width:30px;height:30px;display:grid;place-items:center;flex:0 0 30px;border-radius:10px;background:linear-gradient(145deg,#1688ff,#5b5df0);color:#fff;font-size:15px;box-shadow:0 5px 12px rgba(22,136,255,.28)}.payroll-journey-title{min-width:0;flex:1}.payroll-journey-name{font-size:12px;font-weight:900;letter-spacing:.1px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.payroll-journey-destination{margin-top:1px;color:#718096;font-size:8px;font-weight:800;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.payroll-journey-status{display:inline-flex;align-items:center;gap:4px;padding:4px 6px;border-radius:999px;font-size:7px;font-weight:900;letter-spacing:.2px;white-space:nowrap;background:#e9f9ef;color:#168447;border:1px solid rgba(22,132,71,.12)}.payroll-journey-status.live{background:#eaf4ff;color:#1268cf;border-color:rgba(18,104,207,.12)}.payroll-journey-status .dot{width:5px;height:5px;border-radius:50%;background:currentColor;box-shadow:0 0 0 2px rgba(22,132,71,.10)}.payroll-journey-status.live .dot{box-shadow:0 0 0 2px rgba(18,104,207,.10);animation:payrollJourneyPulse 1.5s ease-in-out infinite}@keyframes payrollJourneyPulse{0%,100%{opacity:.55;transform:scale(.85)}50%{opacity:1;transform:scale(1.1)}}.payroll-journey-progress{height:4px;border-radius:99px;background:#e8edf4;overflow:hidden;margin:1px 0 6px}.payroll-journey-progress>span{display:block;height:100%;width:68%;border-radius:inherit;background:linear-gradient(90deg,#1688ff,#35b8ff,#625cff);box-shadow:0 0 8px rgba(22,136,255,.28)}.payroll-journey-metrics{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:4px}.payroll-journey-metric{min-width:0;min-height:29px;padding:4px 4px 3px;border-radius:9px;background:rgba(244,247,251,.92);border:1px solid rgba(19,43,77,.07);text-align:center}.payroll-journey-icon{font-size:10px;line-height:1;margin-bottom:2px}.payroll-journey-label{font-size:6.5px;text-transform:uppercase;letter-spacing:.35px;font-weight:800;color:#8491a5;line-height:1}.payroll-journey-value{margin-top:2px;font-size:9px;line-height:1;font-weight:900;color:#172238;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.payroll-journey-road{display:flex;align-items:center;gap:5px;margin-top:5px;padding:5px 6px;border-radius:9px;background:rgba(22,136,255,.07);border:1px solid rgba(22,136,255,.10);color:#2f5f8e;min-height:22px}.payroll-journey-road-icon{font-size:11px}.payroll-journey-road-text{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-weight:800;font-size:8px}.payroll-journey-road-caption{display:inline;color:#8292a7;font-size:6px;text-transform:uppercase;letter-spacing:.3px;font-weight:800;margin-right:3px}.payroll-journey-footer{display:flex;justify-content:space-between;align-items:center;margin-top:4px;color:#8491a5;font-size:6.5px;font-weight:800}.payroll-journey-live-dot{color:#18a058}.payroll-journey-arrived .payroll-journey-avatar{background:linear-gradient(145deg,#19a765,#0f8f7a);box-shadow:0 5px 12px rgba(25,167,101,.25)}[data-theme="dark"] .payroll-journey-overlay,[data-bs-theme="dark"] .payroll-journey-overlay{color:#e9f1fb;background:rgba(14,22,35,.91);border-color:rgba(143,177,214,.18);box-shadow:0 18px 44px rgba(0,0,0,.46),0 3px 12px rgba(0,0,0,.28)}[data-theme="dark"] .payroll-journey-card,[data-bs-theme="dark"] .payroll-journey-card{background:linear-gradient(145deg,rgba(19,29,45,.97),rgba(11,20,34,.94))}[data-theme="dark"] .payroll-journey-destination,[data-bs-theme="dark"] .payroll-journey-destination,[data-theme="dark"] .payroll-journey-label,[data-bs-theme="dark"] .payroll-journey-label,[data-theme="dark"] .payroll-journey-footer,[data-bs-theme="dark"] .payroll-journey-footer{color:#8fa4bb}[data-theme="dark"] .payroll-journey-metric,[data-bs-theme="dark"] .payroll-journey-metric{background:rgba(28,41,59,.74);border-color:rgba(143,177,214,.12)}[data-theme="dark"] .payroll-journey-value,[data-bs-theme="dark"] .payroll-journey-value{color:#edf5ff}[data-theme="dark"] .payroll-journey-progress,[data-bs-theme="dark"] .payroll-journey-progress{background:#263548}[data-theme="dark"] .payroll-journey-road,[data-bs-theme="dark"] .payroll-journey-road{background:rgba(44,145,255,.11);border-color:rgba(44,145,255,.18);color:#a8d3ff}[data-theme="dark"] .payroll-journey-status.live,[data-bs-theme="dark"] .payroll-journey-status.live{background:rgba(39,139,255,.15);color:#7dc0ff;border-color:rgba(39,139,255,.22)}[data-theme="dark"] .payroll-journey-status,[data-bs-theme="dark"] .payroll-journey-status{background:rgba(35,176,108,.14);color:#6ee2a7;border-color:rgba(35,176,108,.20)}@media (max-width:520px){.payroll-journey-overlay{left:6px;top:6px;width:calc(100% - 12px);max-width:calc(100% - 12px);border-radius:12px!important}.payroll-journey-card{padding:5px 6px 4px}.payroll-journey-top{gap:5px;margin-bottom:3px;min-height:24px}.payroll-journey-avatar{width:22px;height:22px;flex-basis:22px;border-radius:7px;font-size:11px;box-shadow:0 3px 8px rgba(22,136,255,.22)}.payroll-journey-name{font-size:10px}.payroll-journey-destination{font-size:6px;margin-top:0}.payroll-journey-status{font-size:6px;padding:3px 4px;gap:3px}.payroll-journey-status .dot{width:4px;height:4px}.payroll-journey-progress{display:none}.payroll-journey-metrics{grid-template-columns:repeat(3,minmax(0,1fr));gap:2px;margin-top:2px}.payroll-journey-metric{padding:3px 2px 2px;min-height:25px;border-radius:7px}.payroll-journey-metric:nth-child(n+4){display:none}.payroll-journey-icon{font-size:8px;margin-bottom:1px}.payroll-journey-label{font-size:5px;letter-spacing:.2px}.payroll-journey-value{margin-top:1px;font-size:8px}.payroll-journey-road,.payroll-journey-footer{display:none}}@media (max-width:360px){.payroll-journey-overlay{left:5px;top:5px;width:calc(100% - 10px);max-width:calc(100% - 10px)}.payroll-journey-card{padding:4px 5px 3px}.payroll-journey-top{gap:4px;margin-bottom:2px;min-height:22px}.payroll-journey-avatar{width:20px;height:20px;flex-basis:20px;font-size:10px;border-radius:6px}.payroll-journey-name{font-size:9px}.payroll-journey-destination{display:none}.payroll-journey-status{font-size:5.5px;padding:2px 4px}.payroll-journey-metric{min-height:23px;padding:2px 1px}.payroll-journey-icon{font-size:7px}.payroll-journey-label{font-size:4.5px}.payroll-journey-value{font-size:7.5px}}
`;
        document.head.appendChild(style);
    }
    let overlay = mapElement.querySelector(`.${className}`);
    if (overlay) return overlay;
    mapElement.style.position = 'relative';
    overlay = document.createElement('div');
    overlay.className = `${className} payroll-journey-overlay`;
    mapElement.appendChild(overlay);
    return overlay;
};

window.payrollRenderJourneyOverlay = function (overlay, data) {
    if (!overlay) return;
    const escapeHtml =
        typeof window.payrollEscapeHtml === 'function'
            ? window.payrollEscapeHtml
            : function (value) {
                return String(value ?? '').replace(/[&<>"']/g, function (ch) {
                    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[ch];
                });
            };
    const name = escapeHtml(data.name || 'Employee');
    const road = escapeHtml(data.road || 'Calculating road route...');
    const distance = window.payrollFormatRouteDistance(data.distanceMeters);
    const eta = data.durationSeconds > 0 ? window.payrollFormatRouteDuration(data.durationSeconds) : 'Calculating...';
    const speed = window.payrollFormatSpeed(data.speedMps);
    const accuracy = Number(data.accuracyMeters) > 0 ? `±${Math.round(Number(data.accuracyMeters))} m` : 'Unknown';
    const elapsed = data.journeyStartedAt ? window.payrollFormatRouteDuration((Date.now() - data.journeyStartedAt) / 1000) : '0s';
    const status = data.arrived ? 'ARRIVED' : 'LIVE';
    const statusClass = data.arrived ? '' : ' live';
    const cardClass = data.arrived ? ' payroll-journey-arrived' : '';
    overlay.innerHTML =
        `<div class="payroll-journey-card${cardClass}">` +
        `<div class="payroll-journey-top">` +
        `<div class="payroll-journey-avatar">${data.arrived ? '🏁' : '🛵'}</div>` +
        `<div class="payroll-journey-title"><div class="payroll-journey-name">${name}</div><div class="payroll-journey-destination">📍 Destination • To Office</div></div>` +
        `<div class="payroll-journey-status${statusClass}"><span class="dot"></span>${status}</div>` +
        `</div>` +
        `<div class="payroll-journey-progress"><span></span></div>` +
        `<div class="payroll-journey-metrics">` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">📏</div><div class="payroll-journey-label">Remaining</div><div class="payroll-journey-value">${distance}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">⏱️</div><div class="payroll-journey-label">ETA</div><div class="payroll-journey-value">${eta}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">🚦</div><div class="payroll-journey-label">Speed</div><div class="payroll-journey-value">${escapeHtml(speed)}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">🎯</div><div class="payroll-journey-label">Accuracy</div><div class="payroll-journey-value">${accuracy}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">🕐</div><div class="payroll-journey-label">Journey</div><div class="payroll-journey-value">${elapsed}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">🛣️</div><div class="payroll-journey-label">Route</div><div class="payroll-journey-value">Road</div></div>` +
        `</div>` +
        `<div class="payroll-journey-road"><span class="payroll-journey-road-icon">🛣️</span><div class="payroll-journey-road-text"><span class="payroll-journey-road-caption">Current road</span>${road}</div></div>` +
        `<div class="payroll-journey-footer"><span class="payroll-journey-live-dot">● GPS LIVE</span><span>🏢 Office destination</span></div>` +
        `</div>`;
};

window.payrollRequestJourneyRoute = async function (state, from, to, options = {}) {
    if (!state) return null;
    state.routeState = state.routeState || {};
    const rs = state.routeState;
    const now = Date.now();
    const minMove = Number(options.minMoveMeters) || 20;
    const minInterval = Number(options.minIntervalMs) || 20000;
    const moved = rs.lastRoutedPosition ? window.payrollHaversineMeters(rs.lastRoutedPosition, from) : Infinity;
    if (rs.pending) return rs.route || null;
    if (rs.route && now - (rs.lastRequestedAt || 0) < minInterval) return rs.route;
    rs.pending = true;
    rs.lastRequestedAt = now;
    rs.controller?.abort();
    rs.controller = new AbortController();
    try {
        const route = await window.payrollFetchRoadRoute(from, to, { controller: rs.controller });
        if (route) { rs.route = route; rs.lastRoutedPosition = [from[0], from[1]]; }
        return route || rs.route || null;
    } catch (error) {
        if (error?.name !== 'AbortError') console.warn('Road route unavailable:', error);
        return rs.route || null;
    } finally { rs.pending = false; }
};

// ============================================================
// EMPLOYEE GEO MAP
// FINAL ROBUST VERSION
// ============================================================

window.payrollGeoMaps =
    window.payrollGeoMaps || {};

window.updateGeoMap = async function (
    mapId,
    officeLat,
    officeLng,
    userLat,
    userLng,
    radius,
    isWithin,
    employeeName,
    sessionStartedIso,
    accuracyMeters
) {
    const officeLatitude = Number(officeLat);
    const officeLongitude = Number(officeLng);
    const userLatitude = Number(userLat);
    const userLongitude = Number(userLng);
    const allowedRadius = Number(radius) || 100;

    // --------------------------------------------------------
    // VALIDATE COORDINATES
    // --------------------------------------------------------

    if (
        !Number.isFinite(officeLatitude) ||
        !Number.isFinite(officeLongitude) ||
        !Number.isFinite(userLatitude) ||
        !Number.isFinite(userLongitude)
    ) {
        console.warn(
            "Employee geo map: invalid coordinates."
        );

        return false;
    }

    // --------------------------------------------------------
    // FIND MAP CONTAINER
    // --------------------------------------------------------

    const mapElement =
        document.getElementById(mapId);

    if (!mapElement) {
        console.warn(
            "Employee geo map element not found:",
            mapId
        );

        return false;
    }

    try {

        // ----------------------------------------------------
        // LOAD LEAFLET
        // ----------------------------------------------------

        await window.loadPayrollLeaflet();

        if (!window.L) {
            throw new Error(
                "Leaflet library is not available."
            );
        }

        const office = [
            officeLatitude,
            officeLongitude
        ];

        const user = [
            userLatitude,
            userLongitude
        ];

        let mapData =
            window.payrollGeoMaps[mapId];

        // ----------------------------------------------------
        // PROTECT AGAINST BLOZOR DOM REPLACEMENT
        // ----------------------------------------------------

        if (
            mapData &&
            mapData.map &&
            mapData.map.getContainer() !== mapElement
        ) {
            try {
                mapData.map.remove();
            }
            catch {
            }

            delete window.payrollGeoMaps[mapId];

            mapData = null;
        }

        // ----------------------------------------------------
        // CREATE MAP
        // ----------------------------------------------------

        if (!mapData) {

            // Remove any stale Leaflet state attached
            // to this exact DOM element.
            if (mapElement._leaflet_id) {
                try {
                    delete mapElement._leaflet_id;
                }
                catch {
                }
            }

            const map =
                L.map(
                    mapElement,
                    {
                        zoomControl: true,
                        attributionControl: true,
                        preferCanvas: false
                    }
                );

            // ------------------------------------------------
            // OPEN STREET MAP
            // ------------------------------------------------

            const tileLayer =
                L.tileLayer(
                    "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
                    {
                        maxZoom: 19,
                        attribution:
                            "© OpenStreetMap contributors"
                    }
                );

            tileLayer.addTo(map);

            // ------------------------------------------------
            // OFFICE ICON
            // ------------------------------------------------

            const officeIcon =
                L.divIcon({
                    className:
                        "payroll-office-marker",

                    html:
                        '<div class="payroll-map-office">' +
                        '<i class="bi bi-building-fill"></i>' +
                        '</div>',

                    iconSize: [
                        36,
                        36
                    ],

                    iconAnchor: [
                        18,
                        18
                    ]
                });

    // ------------------------------------------------
    // USER ICON (IDENTITY BASED PIN)
    // ------------------------------------------------

    const rawName = String(employeeName || 'You').trim();
    const nameParts = rawName.split(/\s+/).filter(Boolean);
    const initials = nameParts.length === 1
        ? nameParts[0].slice(0, 1)
        : (nameParts[0][0] + nameParts[nameParts.length - 1][0]);

    const avatarClass = isWithin ? 'within' : 'outside';

    const userIcon =
        L.divIcon({
            className: "payroll-user-marker",
            html:
                '<div class="payroll-map-user payroll-map-user-' + avatarClass + '">' +
                '<span class="payroll-map-user-initials">' + window.escapeAdminHtml(initials.toUpperCase()) + '</span>' +
                '<span class="payroll-map-user-status"></span>' +
                '</div>',
            iconSize: [46, 54],
            iconAnchor: [23, 54]
        });

            // ------------------------------------------------
            // OFFICE MARKER
            // ------------------------------------------------

            const officeMarker =
                L.marker(
                    office,
                    {
                        icon:
                            officeIcon
                    }
                )
                    .addTo(map);

            officeMarker.bindPopup(
                "<b>OFFICE</b><br>Configured location"
            );

            // ------------------------------------------------
            // USER MARKER
            // ------------------------------------------------

            const userMarker =
                L.marker(
                    user,
                    {
                        icon:
                            userIcon
                    }
                )
                    .addTo(map);

            userMarker.bindPopup(
                "<b>YOU</b><br>Current location"
            );
            userMarker.bindTooltip(employeeName || 'You', {
                permanent: true, direction: 'top', offset: [0, -30], className: 'payroll-employee-name-label'
            });

            // ------------------------------------------------
            // ROUTE LINE
            // ------------------------------------------------

            const routeLine = L.polyline([office, user], { color: "#0d6efd", weight: 3, opacity: 0.9, dashArray: "7,7" }).addTo(map);

            const roadRouteCasing = L.polyline([office, user], { color: '#ffffff', weight: 8, opacity: .78, lineCap: 'round', lineJoin: 'round' }).addTo(map);
            const roadRouteLine = L.polyline([office, user], { color: '#1688ff', weight: 5, opacity: .98, lineCap: 'round', lineJoin: 'round' }).addTo(map);
            const journeyOverlay = window.payrollCreateJourneyOverlay(mapElement, 'payroll-employee-journey-overlay');

            // ------------------------------------------------
            // GEOFENCE CIRCLE
            // ------------------------------------------------

            const rangeColor =
                isWithin
                    ? "#198754"
                    : "#dc3545";

            const radiusCircle =
                L.circle(
                    office,
                    {
                        radius:
                            allowedRadius,

                        color:
                            rangeColor,

                        weight:
                            1,

                        fillColor:
                            rangeColor,

                        fillOpacity:
                            0.08
                    }
                )
                    .addTo(map);

            // ------------------------------------------------
            // SAVE MAP STATE
            // ------------------------------------------------

            mapData = {
                map:
                    map,

                tileLayer:
                    tileLayer,

                officeMarker:
                    officeMarker,

                userMarker:
                    userMarker,

                routeLine: routeLine,
                roadRouteCasing: roadRouteCasing,
                roadRouteLine: roadRouteLine,
                journeyOverlay: journeyOverlay,
                routeState: {},
                journeyStartedAt: (sessionStartedIso && !Number.isNaN(Date.parse(sessionStartedIso))) ? Date.parse(sessionStartedIso) : Date.now(),
                employeeName: employeeName || 'You',
                lastRawPosition: user.slice(),
                lastRawPositionAt: Date.now(),
                speedMps: 0,
                lastAccuracyMeters: 0,

                radiusCircle:
                    radiusCircle,

                office:
                    office,

                radius:
                    allowedRadius,

                isWithin:
                    !!isWithin,

                hasInitialView:
                    false,

                lastLiveUpdateAt:
                    0
            };

            window.payrollGeoMaps[mapId] =
                mapData;
        }

        // ----------------------------------------------------
        // UPDATE POSITIONS
        // ----------------------------------------------------

        mapData.officeMarker
            .setLatLng(office);

        // Apply a pending browser-side GPS point, if the watcher reported
        // it before the Blazor component finished creating the map.
        const pendingEmployeePoint =
            window.payrollGeoLivePending?.[mapId];

        if (pendingEmployeePoint &&
            Number.isFinite(Number(pendingEmployeePoint.latitude)) &&
            Number.isFinite(Number(pendingEmployeePoint.longitude))) {
            user[0] = Number(pendingEmployeePoint.latitude);
            user[1] = Number(pendingEmployeePoint.longitude);
            delete window.payrollGeoLivePending[mapId];
        }

        mapData.employeeName = employeeName || mapData.employeeName || 'You';
        try { mapData.userMarker.getTooltip()?.setContent(mapData.employeeName); } catch { }
        if (sessionStartedIso && !Number.isNaN(Date.parse(sessionStartedIso))) mapData.journeyStartedAt = Date.parse(sessionStartedIso);
        mapData.lastAccuracyMeters = Number(accuracyMeters) || mapData.lastAccuracyMeters || 0;

        const rawNow = Date.now();
        if (mapData.lastRawPosition) {
            const seconds = Math.max(.25, (rawNow - (mapData.lastRawPositionAt || rawNow)) / 1000);
            mapData.speedMps = Math.min(55, window.payrollHaversineMeters(mapData.lastRawPosition, user) / seconds);
        }
        mapData.lastRawPosition = user.slice();
        mapData.lastRawPositionAt = rawNow;

        // Presentation road routing: update UI when route loads, but don't block map readiness.
        window.payrollRequestJourneyRoute(mapData, user, office, { minMoveMeters: 20, minIntervalMs: 18000 }).then(function(employeeRoute) {
            if (employeeRoute?.geometry?.length > 1) {
                mapData.roadRouteCasing.setLatLngs(employeeRoute.geometry);
                mapData.roadRouteLine.setLatLngs(employeeRoute.geometry);
                mapData.routeLine.setStyle({ opacity: 0 });
            } else {
                mapData.routeLine.setStyle({ opacity: .9 });
            }
            const employeeRemaining = employeeRoute?.distanceMeters || window.payrollHaversineMeters(user, office);
            window.payrollRenderJourneyOverlay(mapData.journeyOverlay, {
                name: mapData.employeeName, distanceMeters: employeeRemaining,
                durationSeconds: employeeRoute?.durationSeconds || 0, speedMps: mapData.speedMps,
                accuracyMeters: mapData.lastAccuracyMeters, journeyStartedAt: mapData.journeyStartedAt,
                road: window.payrollGetNextRoadName(employeeRoute), arrived: employeeRemaining <= Math.max(25, allowedRadius)
            });
        }).catch(function() { });

        // Initial overlay render (Air distance fallback while routing loads)
        const airRemaining = window.payrollHaversineMeters(user, office);
        window.payrollRenderJourneyOverlay(mapData.journeyOverlay, {
            name: mapData.employeeName, distanceMeters: airRemaining,
            durationSeconds: 0, speedMps: mapData.speedMps,
            accuracyMeters: mapData.lastAccuracyMeters, journeyStartedAt: mapData.journeyStartedAt,
            road: 'Calculating road route...', arrived: airRemaining <= Math.max(25, allowedRadius)
        });

        const employeeAnimationKey =
            'employee:' + mapId;

        if (!mapData.hasInitialView) {
            mapData.userMarker.setLatLng(user);
            mapData.routeLine.setLatLngs([office, user]);
        }
        else {
            window.payrollSmoothMoveMarker(
                mapData.userMarker,
                employeeAnimationKey,
                user,
                900,
                function (position) {
                    mapData.routeLine.setLatLngs([
                        office,
                        position
                    ]);
                }
            );
        }

        mapData.radiusCircle
            .setLatLng(office);

        mapData.radiusCircle
            .setRadius(
                allowedRadius
            );

        // ----------------------------------------------------
        // UPDATE GEOFENCE COLOR
        // ----------------------------------------------------

        const rangeColor =
            isWithin
                ? "#198754"
                : "#dc3545";

        mapData.radiusCircle
            .setStyle({
                color:
                    rangeColor,

                fillColor:
                    rangeColor
            });

        // ----------------------------------------------------
        // FIT OFFICE + USER
        // ----------------------------------------------------

        if (!mapData.hasInitialView) {
            const bounds =
                L.latLngBounds([
                    office,
                    user
                ]);

            if (bounds.isValid()) {
                mapData.map.fitBounds(
                    bounds,
                    {
                        padding: [25, 25],
                        maxZoom: 17,
                        animate: false
                    }
                );
            }
            else {
                mapData.map.setView(user, 17);
            }

            mapData.hasInitialView = true;
        }

        // ----------------------------------------------------
        // FORCE LEAFLET RESIZE
        // ----------------------------------------------------

        const resizeMap =
            function () {

                try {

                    // Blazor can replace the map DOM node during a render.
                    // Never call Leaflet invalidateSize() on a detached/stale map.
                    if (
                        !mapData ||
                        !mapData.map ||
                        !mapData.map.getContainer
                    ) {
                        return;
                    }

                    const currentContainer =
                        mapData.map.getContainer();

                    if (
                        currentContainer !== mapElement ||
                        !document.documentElement.contains(currentContainer) ||
                        !mapData.map._loaded ||
                        !mapData.map._mapPane ||
                        !mapData.map._mapPane._leaflet_pos
                    ) {
                        return;
                    }

                    mapData.map.invalidateSize({
                        pan: false,
                        animate: false,
                        debounceMoveend: true
                    });

                }
                catch (error) {

                    // Resize is presentation-only. A transient Leaflet/DOM
                    // state must never interrupt GPS or attendance updates.
                    console.debug(
                        "Employee map resize skipped:",
                        error
                    );

                }
            };

        // Immediate
        resizeMap();

        // After layout
        requestAnimationFrame(
            function () {
                resizeMap();
            }
        );

        // After browser paint
        setTimeout(
            resizeMap,
            100
        );

        setTimeout(
            resizeMap,
            300
        );

        setTimeout(
            resizeMap,
            700
        );

        mapData.office = office;
    mapData.radius = allowedRadius;
    mapData.isWithin = !!isWithin;

    return true;

    }
    catch (error) {

        console.error(
            "Employee geo map initialization failed:",
            error
        );

        return false;
    }
};




// ============================================================
// DIRECT EMPLOYEE LIVE MAP UPDATE
// ============================================================
// Called by the persistent GPS watcher directly in the browser. This
// keeps the employee's own map moving even when the Blazor circuit is
// busy or between server-side renders.
// ============================================================

window.updateEmployeeLiveGeoMap =
    function (employeeId, latitude, longitude) {
        const id = Number(employeeId);
        const lat = Number(latitude);
        const lng = Number(longitude);

        if (
            !Number.isFinite(id) ||
            id <= 0 ||
            !Number.isFinite(lat) ||
            !Number.isFinite(lng)
        ) {
            return false;
        }

        const mapId = 'geo-map-' + id;
        const mapData =
            window.payrollGeoMaps?.[mapId];

        window.payrollGeoLivePending =
            window.payrollGeoLivePending || {};

        if (!mapData || !mapData.map || !mapData.userMarker) {
            window.payrollGeoLivePending[mapId] = {
                latitude: lat,
                longitude: lng,
                receivedAt: Date.now()
            };
            return false;
        }

        const target = [lat, lng];
        const now = Date.now();
        const previousAt =
            Number(mapData.lastLiveUpdateAt) || 0;

        const elapsed = previousAt > 0
            ? now - previousAt
            : 4500;

        mapData.lastLiveUpdateAt = now;

        const duration = Math.max(
            900,
            Math.min(
                4800,
                elapsed > 250
                    ? elapsed * 0.9
                    : 2200
            )
        );

        const office =
            mapData.office ||
            [mapData.officeMarker.getLatLng().lat, mapData.officeMarker.getLatLng().lng];

        if (mapData.lastRawPosition) {
            const seconds = Math.max(.25, (now - (mapData.lastRawPositionAt || now)) / 1000);
            mapData.speedMps = Math.min(55, window.payrollHaversineMeters(mapData.lastRawPosition, target) / seconds);
        }
        mapData.lastRawPosition = target.slice();
        mapData.lastRawPositionAt = now;

        window.payrollRequestJourneyRoute(mapData, target, office, { minMoveMeters: 20, minIntervalMs: 18000 }).then(function(route) {
            if (route?.geometry?.length > 1) {
                mapData.roadRouteCasing?.setLatLngs(route.geometry);
                mapData.roadRouteLine?.setLatLngs(route.geometry);
                mapData.routeLine?.setStyle({ opacity: 0 });
            }
            const remaining = route?.distanceMeters || window.payrollHaversineMeters(target, office);
            window.payrollRenderJourneyOverlay(mapData.journeyOverlay, {
                name: mapData.employeeName || 'You', distanceMeters: remaining, durationSeconds: route?.durationSeconds || 0,
                speedMps: mapData.speedMps, accuracyMeters: mapData.lastAccuracyMeters, journeyStartedAt: mapData.journeyStartedAt,
                road: window.payrollGetNextRoadName(route), arrived: remaining <= Math.max(25, Number(mapData.radius) || 100)
            });
        }).catch(function() {});

        window.payrollSmoothMoveMarker(
            mapData.userMarker,
            'employee:' + mapId,
            target,
            duration,
            function (position) {
                try {
                    mapData.routeLine.setLatLngs([office, position]);
                    const route = mapData.routeState?.route;
                    const remaining = route?.distanceMeters || window.payrollHaversineMeters(position, office);
                    window.payrollRenderJourneyOverlay(mapData.journeyOverlay, {
                        name: mapData.employeeName || 'You', distanceMeters: remaining, durationSeconds: route?.durationSeconds || 0,
                        speedMps: mapData.speedMps, accuracyMeters: mapData.lastAccuracyMeters, journeyStartedAt: mapData.journeyStartedAt,
                        road: window.payrollGetNextRoadName(route), arrived: remaining <= Math.max(25, Number(mapData.radius) || 100)
                    });
                } catch { }
            }
        );

        return true;
    };

// ============================================================
// DESTROY EMPLOYEE MAP
// ============================================================

window.destroyGeoMap =
    function (mapId) {

        window.payrollCancelGeoAnimation?.(
            'employee:' + mapId);

        const mapData =
            window.payrollGeoMaps[mapId];


        if (!mapData) {
            return;
        }


        try { mapData.routeState?.controller?.abort(); } catch { }

        try {

            mapData.map.remove();

        }
        catch {
        }


        delete window.payrollGeoMaps[mapId];
    };

// ============================================================
// ADMIN LIVE STAFF MAP
// ============================================================

window.adminLiveMaps = {};

/*
 * Visually fan out co-located admin staff markers without changing their
 * actual GPS coordinates. Routes and journey calculations continue to use
 * the real position.
 */
window.payrollBuildAdminMarkerDisplayPositions = function (map, liveStaff, selectedId) {
    const items = [];
    const byId = {};
    const useCollisionOffsets = Number(selectedId) <= 0;

    (Array.isArray(liveStaff) ? liveStaff : []).forEach(function (x) {
        const employeeId = Number(x.employeeId);
        const lat = Number(x.latitude);
        const lng = Number(x.longitude);
        if (!Number.isFinite(employeeId) || !Number.isFinite(lat) || !Number.isFinite(lng)) return;
        const item = { employeeId, lat, lng, offsetX: 0, offsetY: 0 };
        items.push(item);
        byId[employeeId] = item;
    });

    if (!useCollisionOffsets || items.length < 2) return byId;

    // Group staff whose map markers would visually collide.
    const collisionMeters = 45;
    const parent = items.map(function (_, i) { return i; });
    function find(i) {
        while (parent[i] !== i) {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }
    function union(a, b) {
        const ra = find(a), rb = find(b);
        if (ra !== rb) parent[rb] = ra;
    }

    for (let i = 0; i < items.length; i++) {
        for (let j = i + 1; j < items.length; j++) {
            const d = window.payrollHaversineMeters(
                [items[i].lat, items[i].lng],
                [items[j].lat, items[j].lng]
            );
            if (d <= collisionMeters) union(i, j);
        }
    }

    const groups = {};
    items.forEach(function (item, index) {
        const root = find(index);
        if (!groups[root]) groups[root] = [];
        groups[root].push(item);
    });

    Object.keys(groups).forEach(function (root) {
        const group = groups[root];
        if (group.length < 2) return;

        // Stable employee-ID ordering prevents markers from swapping places.
        group.sort(function (a, b) { return a.employeeId - b.employeeId; });
        const center = [group[0].lat, group[0].lng];
        const centerPoint = map.latLngToLayerPoint(center);
        const count = group.length;
        const radius = count <= 2 ? 28 : count <= 4 ? 34 : count <= 7 ? 40 : 46;

        group.forEach(function (item, index) {
            const angle = (-Math.PI / 2) + (index * (Math.PI * 2 / count));
            const point = L.point(
                centerPoint.x + Math.cos(angle) * radius,
                centerPoint.y + Math.sin(angle) * radius
            );
            const display = map.layerPointToLatLng(point);
            item.offsetX = display.lng - item.lng;
            item.offsetY = display.lat - item.lat;
        });
    });

    return byId;
};

window.ensureAdminLiveMapLayout = function (mapId) {
    try {
        var state = window.adminLiveMaps && window.adminLiveMaps[mapId];
        if (!state || !state.map) return;

        var map = state.map;
        var container = map.getContainer();

        if (!state._layoutObserver && typeof ResizeObserver !== 'undefined') {
            state._layoutObserver = new ResizeObserver(function () {
                try { map.invalidateSize(true); } catch (e) { }
            });
            state._layoutObserver.observe(container);
        }

        // The Live Staff panel can become measurable only after Blazor has
        // completed its first layout. Multiple passes prevent the initial
        // grey Leaflet canvas that appears until an employee is selected.
        [0, 50, 150, 300, 600, 1000].forEach(function (delay) {
            setTimeout(function () {
                try {
                    map.invalidateSize(true);

                    // If the first render happened while the responsive grid
                    // was still measuring, invalidateSize alone leaves the
                    // original fitBounds calculated from a zero/partial map.
                    // Refit from the actual live markers after the container
                    // has a stable size. This does not alter map behaviour or
                    // selection; it only completes the initial render.
                    if (state.lastSelectedId === 0) {
                        var points = [];
                        if (state.officeMarker) {
                            points.push(state.officeMarker.getLatLng());
                        }
                        Object.keys(state.markers || {}).forEach(function (id) {
                            try {
                                var marker = state.markers[id];
                                if (marker && marker.getLatLng) {
                                    points.push(marker.getLatLng());
                                }
                            } catch (e) { }
                        });
                        if (points.length > 1) {
                            map.fitBounds(L.latLngBounds(points), {
                                padding: [35, 35],
                                maxZoom: 17,
                                animate: false
                            });
                        } else if (points.length === 1) {
                            map.setView(points[0], 17, { animate: false });
                        }
                    }
                } catch (e) {
                    console.warn("Leaflet initial layout recovery failed:", e);
                }
            }, delay);
        });
    } catch (e) {
        console.warn('Admin live map layout refresh failed:', e);
    }
};


// ============================================================
// ADMIN LIVE LOCATION - DIRECT REALTIME GPS BRIDGE
// ============================================================
//
// The AttendanceRefresh SignalR connection already receives
// LocationChanged events. This bridge consumes the browser event
// immediately so the existing Leaflet marker can move smoothly
// without waiting for a Blazor render + database round-trip.
//
// It is presentation-only. Authoritative lifecycle/state remains
// in LiveStaffLocationPanel and EmployeeGpsSessions.
// ============================================================

window.registerAdminLiveLocationRealtime = function (mapId) {
    if (!mapId) return;

    window.__adminLiveRealtime =
        window.__adminLiveRealtime || {};

    const existing =
        window.__adminLiveRealtime[mapId];

    if (existing) {
        return;
    }

    const handler = function (event) {
        try {
            const data = event?.detail;
            if (!data || typeof data !== 'object') {
                return;
            }

            const employeeId =
                Number(data.EmployeeId ?? data.employeeId);

            const latitude =
                Number(data.Latitude ?? data.latitude);

            const longitude =
                Number(data.Longitude ?? data.longitude);

            if (
                !Number.isFinite(employeeId) ||
                employeeId <= 0 ||
                !Number.isFinite(latitude) ||
                !Number.isFinite(longitude) ||
                latitude < -90 || latitude > 90 ||
                longitude < -180 || longitude > 180 ||
                (Math.abs(latitude) < 0.0000001 && Math.abs(longitude) < 0.0000001)
            ) {
                console.warn('Admin live realtime: invalid GPS coordinate ignored.', data);
                return;
            }

            const state =
                window.adminLiveMaps?.[mapId];

            if (!state?.map || !state.markers?.[employeeId]) {
                // The Blazor listener remains responsible for adding a new
                // employee marker or recovering an initial map snapshot.
                return;
            }

            const marker =
                state.markers[employeeId];

            const target = [
                latitude,
                longitude
            ];

            const displayItems =
                typeof window.payrollBuildAdminMarkerDisplayPositions === 'function'
                    ? window.payrollBuildAdminMarkerDisplayPositions(
                        state.map,
                        [{
                            employeeId: employeeId,
                            latitude: latitude,
                            longitude: longitude
                        }],
                        state.lastSelectedId || 0)
                    : {};

            const displayItem =
                displayItems[employeeId];

            const displayTarget = displayItem
                ? [
                    latitude + Number(displayItem.offsetY || 0),
                    longitude + Number(displayItem.offsetX || 0)
                ]
                : target;

            const now = Date.now();
            const previousAt =
                Number(state.realtimeLastAt?.[employeeId]) || 0;

            const elapsed =
                previousAt > 0
                    ? now - previousAt
                    : 2500;

            state.realtimeLastAt =
                state.realtimeLastAt || {};

            state.realtimeLastAt[employeeId] =
                now;

            if (Number.isFinite(data.SpeedMps)) {
                state.markers[employeeId]._speedMps = Number(data.SpeedMps);
            }

            // Match the real GPS cadence while preventing either a jump or
            // an excessively slow animation when the browser/network pauses.
            const duration =
                Math.max(
                    1200,
                    Math.min(
                        8000,
                        elapsed > 250
                            ? elapsed * 0.92
                            : 2500
                    )
                );

            if (typeof window.payrollSmoothMoveMarker === 'function') {
                window.payrollSmoothMoveMarker(
                    marker,
                    'admin:' + mapId + ':' + employeeId,
                    displayTarget,
                    duration,
                    function (animatedPosition) {
                        try {
                            if (state.collisionConnectors?.[employeeId]) {
                                state.collisionConnectors[employeeId].setLatLngs([
                                    target,
                                    animatedPosition
                                ]);
                            }

                            if (state.journeyLabels?.[employeeId]) {
                                state.journeyLabels[employeeId]
                                    .setLatLng(animatedPosition);
                            }

                            // Smoother road route line connection
                            if (state.roadRouteLines?.[employeeId]) {
                                const current = state.roadRouteLines[employeeId].getLatLngs();
                                if (current?.length >= 2) {
                                    const dFirst = window.payrollHaversineMeters([current[0].lat, current[0].lng], animatedPosition);
                                    const dLast = window.payrollHaversineMeters([current[current.length - 1].lat, current[current.length - 1].lng], animatedPosition);
                                    if (dFirst < dLast) {
                                        current[0] = animatedPosition;
                                    } else {
                                        current[current.length - 1] = animatedPosition;
                                    }
                                    state.roadRouteLines[employeeId].setLatLngs(current);
                                }
                            }
                            if (state.roadRouteCasings?.[employeeId]) {
                                const current = state.roadRouteCasings[employeeId].getLatLngs();
                                if (current?.length >= 2) {
                                    const dFirst = window.payrollHaversineMeters([current[0].lat, current[0].lng], animatedPosition);
                                    const dLast = window.payrollHaversineMeters([current[current.length - 1].lat, current[current.length - 1].lng], animatedPosition);
                                    if (dFirst < dLast) {
                                        current[0] = animatedPosition;
                                    } else {
                                        current[current.length - 1] = animatedPosition;
                                    }
                                    state.roadRouteCasings[employeeId].setLatLngs(current);
                                }
                            }
                        }
                        catch { }
                    }
                );
            }
            else {
                marker.setLatLng(displayTarget);
            }

            // Keep the visual journey trail continuous between SignalR fixes.
            if (!Array.isArray(state.trailPoints?.[employeeId])) {
                state.trailPoints =
                    state.trailPoints || {};
                state.trailPoints[employeeId] = [];
            }

            const points =
                state.trailPoints[employeeId];

            const last =
                points[points.length - 1];

            if (
                !last ||
                last[0] !== target[0] ||
                last[1] !== target[1]
            ) {
                points.push(target);

                if (points.length > 120) {
                    points.shift();
                }
            }

            if (state.trails?.[employeeId]) {
                state.trails[employeeId]
                    .setLatLngs(points);
            }

            // Keep the existing live route endpoint synchronized with the
            // actual GPS coordinate, without changing its routing logic.
            if (state.roadRouteLines?.[employeeId]) {
                const current =
                    state.roadRouteLines[employeeId]
                        .getLatLngs();

                if (current?.length >= 2) {
                    current[current.length - 1] =
                        target;
                    state.roadRouteLines[employeeId]
                        .setLatLngs(current);
                }
            }

            if (state.roadRouteCasings?.[employeeId]) {
                const current =
                    state.roadRouteCasings[employeeId]
                        .getLatLngs();

                if (current?.length >= 2) {
                    current[current.length - 1] =
                        target;
                    state.roadRouteCasings[employeeId]
                        .setLatLngs(current);
                }
            }
        }
        catch (error) {
            console.warn(
                'Admin live realtime marker update failed:',
                error
            );
        }
    };

    window.__adminLiveRealtime[mapId] = {
        handler: handler
    };

    window.addEventListener(
        'location-data-changed',
        handler
    );
};

window.unregisterAdminLiveLocationRealtime = function (mapId) {
    const registry =
        window.__adminLiveRealtime;

    if (!registry || !registry[mapId]) {
        return;
    }

    try {
        window.removeEventListener(
            'location-data-changed',
            registry[mapId].handler
        );
    }
    catch { }

    delete registry[mapId];
};

window.updateAdminLiveStaffMap =
    async function (
        mapId,
        officeLat,
        officeLng,
        officeRadius,
        staff,
        selectedId,
        isPlayback,
        dotNetRef
    ) {
        // Defensive fallback for presentation-only escaping. This keeps the
        // live map usable even if a stale browser cache briefly omits the
        // shared helper. It does not affect GPS, attendance, sessions or DB.
        if (typeof window.payrollEscapeHtml !== 'function') {
            window.payrollEscapeHtml = function (value) {
                return String(value ?? '').replace(/[&<>"']/g, function (ch) {
                    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[ch];
                });
            };
        }

        const parsedOfficeLat = Number(officeLat);
        const parsedOfficeLng = Number(officeLng);

        if (
            !Number.isFinite(parsedOfficeLat) ||
            !Number.isFinite(parsedOfficeLng) ||
            parsedOfficeLat === 0 ||
            parsedOfficeLng === 0
        ) {
            console.warn(
                "Admin live map: valid office GPS coordinates are not configured."
            );
            return;
        }

        try {
            await window.loadPayrollLeaflet();

            const office = [
                parsedOfficeLat,
                parsedOfficeLng
            ];

            const liveStaff =
                Array.isArray(staff)
                    ? staff
                    : [];

            let state =
                window.adminLiveMaps[mapId];

            if (!state) {
                const map =
                    L.map(
                        mapId,
                        {
                            zoomControl: true,
                            attributionControl: true
                        }
                    );

                L.tileLayer(
                    'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
                    {
                        maxZoom: 19,
                        attribution:
                            '© OpenStreetMap contributors'
                    }
                ).addTo(map);

                // CRITICAL: Set initial view to prevent "Set map center and zoom first" errors
                // when subsequent operations (like collision offset calc) are called before fitBounds.
                map.setView(office, 15);

                const officeIcon =
                    L.divIcon({
                        className:
                            'payroll-office-marker',
                        html:
                            '<div class="payroll-map-office">' +
                            '<i class="bi bi-building-fill"></i>' +
                            '</div>',
                        iconSize: [38, 38],
                        iconAnchor: [19, 19]
                    });

                const officeMarker =
                    L.marker(
                        office,
                        {
                            icon: officeIcon
                        }
                    ).addTo(map);

                officeMarker.bindTooltip(
                    '<strong>Office</strong><br><span>Configured location</span>',
                    {
                        permanent: false,
                        direction: 'top',
                        offset: [0, -12],
                        sticky: true,
                        className: 'admin-office-tooltip'
                    }
                );

                state = {
                    map: map,
                    officeMarker:
                        officeMarker,
                    circle: null,
                    markers: {},
                    lines: {},
                    trails: {},
                    trailPoints: {},
                    labels: {},
                    journeyLabels: {},
                    collisionConnectors: {},
                    lastOfficeRadius: 0,
                    historyRoute: null,
                    historyMarkers: [],
                    historyStartMarker: null,
                    historyEndMarker: null,
                    hasInitialFit: false,
                    lastStaffSignature: '',
                    lastSelectedId: 0,
                    lastLocationAt: {},
                    routeStates: {},
                    roadRouteLines: {},
                    roadRouteCasings: {},
                    journeyStartedAt: {},
                     realtimeLastAt: {}
                };

                window.adminLiveMaps[mapId] =
                    state;

                window.ensureAdminLiveMapLayout(mapId);
            }

            state.officeMarker
                .setLatLng(office);

            const staffIds =
                new Set(
                    liveStaff.map(
                        function (x) {
                            return Number(
                                x.employeeId
                            );
                        }
                    )
                );

            Object.keys(
                state.markers
            ).forEach(
                function (id) {
                    const employeeId =
                        Number(id);

                    if (
                        !staffIds.has(
                            employeeId
                        )
                    ) {
                        try {
                            state.map.removeLayer(
                                state.markers[id]
                            );
                        }
                        catch { }

                        try {
                            if (
                                state.lines[id]
                            ) {
                                state.map.removeLayer(
                                    state.lines[id]
                                );
                            }
                        }
                        catch { }

                        try {
                            if (state.trails[id]) {
                                state.map.removeLayer(state.trails[id]);
                            }
                        }
                        catch { }

                        try {
                            if (
                                state.labels[id]
                            ) {
                                state.map.removeLayer(
                                    state.labels[id]
                                );
                            }
                        }
                        catch { }

                        delete state.markers[id];
                        delete state.lines[id];
                        delete state.trails[id];
                        delete state.trailPoints[id];
                        delete state.labels[id];
                        delete state.journeyLabels[id];
                        try {
                            if (state.collisionConnectors[id]) {
                                state.map.removeLayer(state.collisionConnectors[id]);
                            }
                        }
                        catch { }
                        delete state.collisionConnectors[id];
                        try { state.roadRouteLines[id] && state.map.removeLayer(state.roadRouteLines[id]); } catch { }
                        try { state.roadRouteCasings[id] && state.map.removeLayer(state.roadRouteCasings[id]); } catch { }
                        try { state.routeStates[id]?.controller?.abort(); } catch { }
                        delete state.roadRouteLines[id];
                        delete state.roadRouteCasings[id];
                        delete state.routeStates[id];
                        delete state.journeyStartedAt[id];
                    }
                }
            );

            const staffSignature =
                liveStaff
                    .map(function (x) {
                        return Number(x.employeeId);
                    })
                    .sort(function (a, b) { return a - b; })
                    .join(',');

            const membershipChanged =
                state.lastStaffSignature !== staffSignature ||
                state.lastSelectedId !== Number(selectedId);

            const markerDisplayPositions =
                window.payrollBuildAdminMarkerDisplayPositions(
                    state.map,
                    liveStaff,
                    selectedId
                );

            liveStaff.forEach(
                function (x) {
                    const employeeId =
                        Number(
                            x.employeeId
                        );

                    const lat =
                        Number(
                            x.latitude
                        );

                    const lng =
                        Number(
                            x.longitude
                        );

                    if (
                        !Number.isFinite(lat) ||
                        !Number.isFinite(lng) ||
                        lat < -90 || lat > 90 ||
                        lng < -180 || lng > 180 ||
                        (Math.abs(lat) < 0.0000001 && Math.abs(lng) < 0.0000001)
                    ) {
                        return;
                    }

                    const position = [
                        lat,
                        lng
                    ];

                    const displayItem = markerDisplayPositions[employeeId];
                    const displayPosition = displayItem
                        ? [
                            lat + Number(displayItem.offsetY || 0),
                            lng + Number(displayItem.offsetX || 0)
                        ]
                        : position.slice();
                    const hasCollisionOffset =
                        !!displayItem &&
                        (Math.abs(Number(displayItem.offsetX || 0)) > 0 ||
                         Math.abs(Number(displayItem.offsetY || 0)) > 0);

                    if (!state.routeStates[employeeId]) {
                        state.routeStates[employeeId] = {};
                    }
                    if (!state.journeyStartedAt[employeeId] && x.sessionStartedUtc) {
                        const parsedStart = Date.parse(x.sessionStartedUtc);
                        if (!Number.isNaN(parsedStart)) state.journeyStartedAt[employeeId] = parsedStart;
                    }

                    const isSelected =
                        Number(selectedId) === employeeId;

                    if (!Array.isArray(state.trailPoints[employeeId])) {
                        state.trailPoints[employeeId] = [];
                    }

                    const points = state.trailPoints[employeeId];
                    const previousPoint = points[points.length - 1];
                    if (!previousPoint ||
                        previousPoint[0] !== position[0] ||
                        previousPoint[1] !== position[1]) {
                        points.push(position);
                        if (points.length > 60) {
                            points.shift();
                        }
                    }

                    const withinRange =
                        Boolean(
                            x.isWithinAllowedRadius
                        );

                    const status =
                        String(
                            x.status || 'Live'
                        ).toLowerCase();

                    const allowedRadius =
                        Number(
                            x.allowedRadiusMeters
                        ) || 100;

                    let markerColor =
                        '#198754';

                    if (!withinRange) {
                        markerColor =
                            '#dc3545';
                    }
                    else if (
                        status === 'stale'
                    ) {
                        markerColor =
                            '#ffc107';
                    }

                    const rawName = String(x.name || 'Employee').trim();
                    const nameParts = rawName.split(/\s+/).filter(Boolean);
                    const initials = nameParts.length === 1
                        ? nameParts[0].slice(0, 1)
                        : (nameParts[0][0] + nameParts[nameParts.length - 1][0]);
                    const avatarClass = withinRange ? 'within' : 'outside';
                    const statusDotClass = status === 'stale' ? ' stale' : (status === 'offline' ? ' offline' : '');

                    const icon =
                        L.divIcon({
                            className: 'payroll-user-marker',
                            html:
                                '<div class="payroll-map-user payroll-map-user-' + avatarClass + '">' +
                                '<span class="payroll-map-user-initials">' + window.escapeAdminHtml(initials.toUpperCase()) + '</span>' +
                                '<span class="payroll-map-user-status' + statusDotClass + '"></span>' +
                                '</div>',
                            iconSize: [46, 54],
                            iconAnchor: [23, 54] // Exact bottom tip anchoring
                        });

                    let markerCreated = false;

                    if (
                        !state.markers[
                        employeeId
                        ]
                    ) {
                        state.markers[
                            employeeId
                        ] =
                            L.marker(
                                displayPosition,
                                {
                                    icon: icon,
                                    keyboard: true,
                                    riseOnHover: true
                                }
                            ).addTo(
                                state.map
                            );

                        if (dotNetRef) {
                            state.markers[employeeId].on('click', function () {
                                try {
                                    dotNetRef.invokeMethodAsync('SelectEmployeeFromMap', employeeId);
                                } catch (e) { }
                            });
                            state.markerDotNetRef = dotNetRef;
                        }

                        markerCreated = true;
                    }
                    else {
                        state.markers[
                            employeeId
                        ].setIcon(
                            icon
                        );

                        // The map may have been created before the Blazor
                        // reference was available. Ensure the click handler
                        // exists on every update without stacking handlers.
                        if (dotNetRef && state.markerDotNetRef !== dotNetRef) {
                            try {
                                state.markers[employeeId].off('click');
                            } catch (e) { }
                            state.markers[employeeId].on('click', function () {
                                try {
                                    dotNetRef.invokeMethodAsync('SelectEmployeeFromMap', employeeId);
                                } catch (e) { }
                            });
                            state.markerDotNetRef = dotNetRef;
                        }

                        const now = Date.now();
                        const previousAt =
                            Number(state.lastLocationAt[employeeId]) || 0;
                        const elapsed = previousAt > 0
                            ? now - previousAt
                            : 4500;

                        state.lastLocationAt[employeeId] = now;

                        const moveDuration = Math.max(
                            900,
                            Math.min(
                                4800,
                                elapsed > 250
                                    ? elapsed * 0.9
                                    : 2200
                            )
                        );

                        window.payrollSmoothMoveMarker(
                            state.markers[employeeId],
                            'admin:' + mapId + ':' + employeeId,
                            displayPosition,
                            moveDuration,
                            function (animatedPosition) {
                                try {
                                    if (state.collisionConnectors[employeeId]) {
                                        state.collisionConnectors[employeeId].setLatLngs([
                                            position,
                                            animatedPosition
                                        ]);
                                    }

                                    if (state.journeyLabels[employeeId]) {
                                        state.journeyLabels[employeeId].setLatLng(animatedPosition);
                                    }

                                    // Update road route endpoint to match visual marker motion
                                    if (state.roadRouteLines?.[employeeId]) {
                                        const current = state.roadRouteLines[employeeId].getLatLngs();
                                        if (current?.length >= 2) {
                                            current[current.length - 1] = animatedPosition;
                                            state.roadRouteLines[employeeId].setLatLngs(current);
                                        }
                                    }
                                    if (state.roadRouteCasings?.[employeeId]) {
                                        const current = state.roadRouteCasings[employeeId].getLatLngs();
                                        if (current?.length >= 2) {
                                            current[current.length - 1] = animatedPosition;
                                            state.roadRouteCasings[employeeId].setLatLngs(current);
                                        }
                                    }

                                    if (state.labels[employeeId]) {
                                        state.labels[employeeId].setLatLng(
                                            window.getAdminLineMidpoint(
                                                office,
                                                position
                                            )
                                        );
                                    }
                                }
                                catch { }
                            }
                        );

                        // Smooth camera focus if this employee is selected
                        // Triggered on every SignalR fix (Blazor render)
                        const isPlaybackActive = typeof isPlayback !== 'undefined' ? isPlayback : false;
                        if (isSelected && !isPlaybackActive) {
                            const bounds = L.latLngBounds([office, position]);
                            state.map.fitBounds(bounds, {
                                padding: [80, 80],
                                maxZoom: 17,
                                animate: true,
                                duration: 1.2
                            });
                        }
                    }

                    if (markerCreated) {
                        state.lastLocationAt[employeeId] = Date.now();
                    }

                    if (points.length > 1) {
                        if (!state.trails[employeeId]) {
                            state.trails[employeeId] = L.polyline(
                                points,
                                {
                                    color: markerColor,
                                    weight: isSelected ? 5 : 3,
                                    opacity: isSelected ? .9 : .45,
                                    dashArray: isSelected ? null : '5,7'
                                }
                            ).addTo(state.map);
                        }
                        else {
                            state.trails[employeeId].setLatLngs(points);
                            state.trails[employeeId].setStyle({
                                color: markerColor,
                                weight: isSelected ? 5 : 3,
                                opacity: isSelected ? .9 : .45,
                                dashArray: isSelected ? null : '5,7'
                            });
                        }
                    }

                    if (hasCollisionOffset) {
                        if (!state.collisionConnectors[employeeId]) {
                            state.collisionConnectors[employeeId] = L.polyline(
                                [position, displayPosition],
                                {
                                    color: markerColor,
                                    weight: 2,
                                    opacity: .72,
                                    dashArray: '3,4',
                                    lineCap: 'round'
                                }
                            ).addTo(state.map);
                        }
                        else {
                            state.collisionConnectors[employeeId].setLatLngs([
                                position,
                                displayPosition
                            ]);
                            state.collisionConnectors[employeeId].setStyle({
                                color: markerColor,
                                opacity: .72
                            });
                        }
                    }
                    else if (state.collisionConnectors[employeeId]) {
                        try {
                            state.map.removeLayer(state.collisionConnectors[employeeId]);
                        }
                        catch { }
                        delete state.collisionConnectors[employeeId];
                    }

                    if (state.trails[employeeId]) {
                        state.trails[employeeId].setStyle({
                            opacity: Number(selectedId) > 0 && !isSelected ? 0 : (isSelected ? .9 : .45)
                        });
                    }

                    if (state.roadRouteLines[employeeId]) {
                        state.roadRouteLines[employeeId].setStyle({
                            color: '#1688ff',
                            weight: isSelected ? 6 : 4,
                            opacity: Number(selectedId) > 0 && !isSelected ? 0 : (isSelected ? .98 : .72)
                        });
                    }
                    if (state.roadRouteCasings[employeeId]) {
                        state.roadRouteCasings[employeeId].setStyle({
                            weight: isSelected ? 9 : 7,
                            opacity: Number(selectedId) > 0 && !isSelected ? 0 : .72
                        });
                    }

                    const distance =
                        window.formatAdminDistance(
                            Number(
                                x.distanceMeters
                            ) || 0
                        );

                    const allowed =
                        Number(
                            x.allowedRadiusMeters
                        ) || 0;

                    const rangeText =
                        withinRange
                            ? 'Within allowed range'
                            : 'Outside allowed range';

                    const safeName =
                        window.escapeAdminHtml(
                            x.name
                        );

                    const routeStateForTooltip = state.routeStates[employeeId] || {};
                    const cachedRouteForTooltip = routeStateForTooltip.route;
                    const tooltipDistance = cachedRouteForTooltip?.distanceMeters > 0
                        ? window.payrollFormatRouteDistance(cachedRouteForTooltip.distanceMeters)
                        : distance;
                    const tooltipEta = cachedRouteForTooltip?.durationSeconds > 0
                        ? window.payrollFormatRouteDuration(cachedRouteForTooltip.durationSeconds)
                        : 'Calculating…';
                    const tooltipSpeed = window.payrollFormatSpeed(x.speedMps || state.markers[employeeId]._speedMps || 0);
                    const tooltipAccuracy = Number(x.accuracyMeters) > 0
                        ? `±${Math.round(Number(x.accuracyMeters))} m`
                        : 'Unknown';
                    const tooltipHtml = window.payrollCreateAdminTooltipHtml(
                        x, initials.toUpperCase(), withinRange, distance, tooltipDistance, tooltipEta, tooltipSpeed
                    );

                    if (state.markers[employeeId].getTooltip()) {
                        state.markers[employeeId].setTooltipContent(tooltipHtml);
                    } else {
                        state.markers[employeeId].bindTooltip(tooltipHtml, {
                            permanent: false,
                            direction: 'top',
                            offset: [0, -24],
                            sticky: true,
                            opacity: .98,
                            className: 'admin-live-hover-tooltip'
                        });
                    }

                    const lineOptions = {
                        color: markerColor,
                        weight: 2,
                        opacity: .8,
                        dashArray: '6,6'
                    };

                    if (!state.roadRouteCasings[employeeId]) {
                        state.roadRouteCasings[employeeId] = L.polyline([], {
                            color: '#ffffff', weight: 7, opacity: .72, lineCap: 'round', lineJoin: 'round'
                        }).addTo(state.map);
                    }
                    if (!state.roadRouteLines[employeeId]) {
                        state.roadRouteLines[employeeId] = L.polyline([], {
                            color: '#1688ff', weight: 4, opacity: .95, lineCap: 'round', lineJoin: 'round'
                        }).addTo(state.map);
                    }

                    // Throttled road routing: actual GPS points trigger route refreshes,
                    // while marker motion remains smoothly interpolated in the browser.
                    const routeState = state.routeStates[employeeId];
                    const routeNow = Date.now();

                    routeState.lastRawPosition = position.slice();
                    routeState.lastRawPositionAt = routeNow;

                    window.payrollRequestJourneyRoute(
                        routeState,
                        position,
                        office,
                        { minMoveMeters: 25, minIntervalMs: 30000 }
                    ).then(function(route) {
                        if (!route || !state.markers[employeeId]) return;
                        const remaining = route.distanceMeters || window.payrollHaversineMeters(state.markers[employeeId].getLatLng(), office);
                        state.roadRouteCasings[employeeId]?.setLatLngs(route.geometry);
                        state.roadRouteLines[employeeId]?.setLatLngs(route.geometry);
                        const routeDistance = window.payrollFormatRouteDistance(remaining);
                        const eta = window.payrollFormatRouteDuration(route.durationSeconds);

                        const distanceElement = document.querySelector(`[data-selected-route-distance="${employeeId}"]`);
                        if (distanceElement) {
                            distanceElement.innerText = routeDistance;
                        }

                        const etaElement = document.querySelector(`[data-selected-eta="${employeeId}"]`);
                        if (etaElement) {
                            etaElement.innerText = eta;
                        }

                        const name = window.payrollEscapeHtml(x.name || 'Employee');
                        const hoverSpeed = window.payrollFormatSpeed(x.speedMps || state.markers[employeeId]._speedMps || 0);
                        const hoverWithin = Boolean(x.isWithinAllowedRadius);
                        const cleanName = name.replace(/<[^>]*>/g, '').trim();
                        const parts = cleanName.split(/\s+/).filter(Boolean);
                        const hoverInitials = initials.toUpperCase();

                        if (state.markers[employeeId].getTooltip()) {
                            state.markers[employeeId].setTooltipContent(
                                window.payrollCreateAdminTooltipHtml(
                                    x, hoverInitials, hoverWithin, distance, routeDistance, eta, hoverSpeed
                                )
                            );
                        }
                    }).catch(function() {});

                    // Throttled road routing
                });

            // --------------------------------------------------------
            // OFFICE GEOFENCE RADIUS
            // --------------------------------------------------------
            // Keep exactly one live circle on the map. The radius comes
            // from the current CompanySettings value on every Blazor
            // refresh, so changing the admin radius updates the circle
            // immediately without requiring a page reload.
            // --------------------------------------------------------
            const parsedRadius = Number(officeRadius);
            const effectiveRadius = Number.isFinite(parsedRadius) && parsedRadius > 0
                ? parsedRadius
                : 100;

            if (!state.circle) {
                state.circle = L.circle(office, {
                    radius: effectiveRadius,
                    color: '#0d6efd',
                    weight: 2,
                    opacity: 0.72,
                    fillColor: '#0d6efd',
                    fillOpacity: 0.08,
                    interactive: false,
                    bubblingMouseEvents: false
                }).addTo(state.map);
            } else {
                state.circle.setLatLng(office);
                state.circle.setRadius(effectiveRadius);
                state.circle.setStyle({
                    color: '#0d6efd',
                    weight: 2,
                    opacity: 0.72,
                    fillColor: '#0d6efd',
                    fillOpacity: 0.08
                });
            }

            state.lastOfficeRadius = effectiveRadius;

            // Keep the geofence above route/trail overlays and below
            // employee/office markers, making it visible at all zooms.
            try {
                state.circle.bringToFront();
            } catch { }

            // Fit map bounds on initial load or selection change
            if (!state.hasInitialFit || membershipChanged) {
                const points = [office];
                liveStaff.forEach(x => {
                    const lat = Number(x.latitude);
                    const lng = Number(x.longitude);
                    if (Number.isFinite(lat) && Number.isFinite(lng)) {
                        points.push([lat, lng]);
                    }
                });

                if (points.length > 1) {
                    const bounds = L.latLngBounds(points);
                    if (bounds.isValid()) {
                        state.map.fitBounds(bounds, {
                            padding: [50, 50],
                            maxZoom: 17,
                            animate: false
                        });
                    }
                } else {
                    state.map.setView(office, 15);
                }
                state.hasInitialFit = true;
            }

            state.lastStaffSignature = staffSignature;
            state.lastSelectedId = Number(selectedId);
        } catch (error) {
            console.error("Admin live map update failed:", error);
        }
    };

// ============================================================
// ADMIN HISTORICAL GPS ROUTE
// ============================================================

window.updateAdminHistoryRoute =
    async function (
        mapId,
        history,
        employeeName
    ) {
        try {
            await window.loadPayrollLeaflet();

            const state =
                window.adminLiveMaps?.[mapId];

            if (!state || !state.map) {
                console.warn(
                    'Admin map not initialized:',
                    mapId
                );
                return;
            }

            window.clearAdminHistoryRoute(
                mapId
            );

            if (
                !Array.isArray(history) ||
                history.length === 0
            ) {
                return;
            }

            const validPoints =
                history
                    .map(
                        function (x, index) {
                            const lat =
                                Number(
                                    x.latitude ??
                                    x.Latitude
                                );

                            const lng =
                                Number(
                                    x.longitude ??
                                    x.Longitude
                                );

                            if (
                                !Number.isFinite(
                                    lat
                                ) ||
                                !Number.isFinite(
                                    lng
                                )
                            ) {
                                return null;
                            }

                            return {
                                index: index,
                                latitude: lat,
                                longitude: lng,
                                distance:
                                    Number(
                                        x.distanceFromOfficeMeters ??
                                        x.DistanceFromOfficeMeters
                                    ) || 0,
                                allowed:
                                    Number(
                                        x.allowedRadiusMeters ??
                                        x.AllowedRadiusMeters
                                    ) || 0,
                                within:
                                    Boolean(
                                        x.isWithinAllowedRadius ??
                                        x.IsWithinAllowedRadius
                                    ),
                                recordedAt:
                                    x.recordedAtUtc ??
                                    x.RecordedAtUtc
                            };
                        }
                    )
                    .filter(
                        function (x) {
                            return x !== null;
                        }
                    );

            if (
                validPoints.length === 0
            ) {
                return;
            }

            const route =
                validPoints.map(
                    function (x) {
                        return [
                            x.latitude,
                            x.longitude
                        ];
                    }
                );

            const routeColor =
                '#0d6efd';

            state.historyRoute =
                L.polyline(
                    route,
                    {
                        color:
                            routeColor,
                        weight: 5,
                        opacity: .85,
                        lineJoin: 'round',
                        lineCap: 'round'
                    }
                ).addTo(
                    state.map
                );

            state.historyMarkers = [];

            validPoints.forEach(
                function (point, index) {
                    const isFirst =
                        index === 0;

                    const isLast =
                        index ===
                        validPoints.length - 1;

                    let markerColor =
                        '#0d6efd';

                    if (isFirst) {
                        markerColor =
                            '#198754';
                    }

                    if (isLast) {
                        markerColor =
                            '#dc3545';
                    }

                    const pointIcon =
                        L.divIcon({
                            className:
                                'payroll-history-point',
                            html:
                                '<div style="' +
                                'width:12px;' +
                                'height:12px;' +
                                'border-radius:50%;' +
                                'background:' +
                                markerColor +
                                ';' +
                                'border:2px solid #fff;' +
                                'box-shadow:0 1px 5px rgba(0,0,0,.35);' +
                                '"></div>',
                            iconSize: [12, 12],
                            iconAnchor: [6, 6]
                        });

                    const marker =
                        L.marker(
                            [
                                point.latitude,
                                point.longitude
                            ],
                            {
                                icon:
                                    pointIcon,
                                zIndexOffset:
                                    isLast
                                        ? 1000
                                        : 100
                            }
                        ).addTo(
                            state.map
                        );

                    const timeText =
                        window.formatAdminHistoryTime(
                            point.recordedAt
                        );

                    const distanceText =
                        window.formatAdminDistance(
                            point.distance
                        );

                    const allowedText =
                        point.allowed > 0
                            ? point.allowed + ' m'
                            : '-';

                    const statusText =
                        point.within
                            ? 'Within allowed range'
                            : 'Outside allowed range';

                    const statusColor =
                        point.within
                            ? '#198754'
                            : '#dc3545';

                    const safeEmployeeName =
                        window.escapeAdminHtml(
                            employeeName ||
                            'Employee'
                        );

                    let title =
                        'GPS Point ' +
                        (index + 1);

                    if (isFirst) {
                        title =
                            'START';
                    }
                    else if (isLast) {
                        title =
                            'LATEST';
                    }

                    marker.bindPopup(
                        '<div style="min-width:210px">' +
                        '<strong>' +
                        safeEmployeeName +
                        '</strong>' +
                        '<hr style="margin:6px 0">' +
                        '<strong>' +
                        title +
                        '</strong><br>' +
                        '<span>Time: ' +
                        timeText +
                        '</span><br>' +
                        '<span>Distance: ' +
                        distanceText +
                        '</span><br>' +
                        '<span>Allowed: ' +
                        allowedText +
                        '</span><br>' +
                        '<span>Latitude: ' +
                        point.latitude.toFixed(6) +
                        '</span><br>' +
                        '<span>Longitude: ' +
                        point.longitude.toFixed(6) +
                        '</span><br>' +
                        '<strong style="color:' +
                        statusColor +
                        '">' +
                        statusText +
                        '</strong>' +
                        '</div>'
                    );

                    marker.bindTooltip(
                        title,
                        {
                            direction: 'top',
                            offset: [0, -8],
                            opacity: .9
                        }
                    );

                    state.historyMarkers.push(
                        marker
                    );
                }
            );

            const first =
                validPoints[0];

            const last =
                validPoints[
                validPoints.length - 1
                ];

            const startIcon =
                L.divIcon({
                    className:
                        'payroll-history-start',
                    html:
                        '<div style="' +
                        'width:30px;' +
                        'height:30px;' +
                        'border-radius:50%;' +
                        'display:flex;' +
                        'align-items:center;' +
                        'justify-content:center;' +
                        'background:#198754;' +
                        'color:#fff;' +
                        'border:3px solid #fff;' +
                        'box-shadow:0 2px 8px rgba(0,0,0,.35);' +
                        'font-size:13px">' +
                        '<i class="bi bi-play-fill"></i>' +
                        '</div>',
                    iconSize: [30, 30],
                    iconAnchor: [15, 15]
                });

            const endIcon =
                L.divIcon({
                    className:
                        'payroll-history-end',
                    html:
                        '<div style="' +
                        'width:34px;' +
                        'height:34px;' +
                        'border-radius:50%;' +
                        'display:flex;' +
                        'align-items:center;' +
                        'justify-content:center;' +
                        'background:#dc3545;' +
                        'color:#fff;' +
                        'border:3px solid #fff;' +
                        'box-shadow:0 2px 8px rgba(0,0,0,.35);' +
                        'font-size:16px">' +
                        '<i class="bi bi-geo-alt-fill"></i>' +
                        '</div>',
                    iconSize: [34, 34],
                    iconAnchor: [17, 17]
                });

            state.historyStartMarker =
                L.marker(
                    [
                        first.latitude,
                        first.longitude
                    ],
                    {
                        icon:
                            startIcon,
                        zIndexOffset:
                            2000
                    }
                ).addTo(
                    state.map
                );

            state.historyStartMarker.bindPopup(
                '<strong>START</strong><br>' +
                window.formatAdminHistoryTime(
                    first.recordedAt
                )
            );

            state.historyEndMarker =
                L.marker(
                    [
                        last.latitude,
                        last.longitude
                    ],
                    {
                        icon:
                            endIcon,
                        zIndexOffset:
                            2100
                    }
                ).addTo(
                    state.map
                );

            state.historyEndMarker.bindPopup(
                '<strong>LATEST LOCATION</strong><br>' +
                window.formatAdminHistoryTime(
                    last.recordedAt
                )
            );

            const bounds =
                L.latLngBounds(
                    route
                );

            state.map.fitBounds(
                bounds,
                {
                    padding: [45, 45],
                    maxZoom: 18
                }
            );

            setTimeout(function () {
                try { state.map.invalidateSize(true); } catch (e) { }
            }, 100);
        }
        catch (error) {
            console.error(
                'Admin history route error:',
                error
            );
        }
    };

// ============================================================
// CLEAR ADMIN HISTORICAL ROUTE
// ============================================================

window.clearAdminHistoryRoute =
    function (mapId) {
        const state =
            window.adminLiveMaps?.[mapId];

        if (!state) return;

        try {
            if (state.historyRoute) {
                state.map.removeLayer(
                    state.historyRoute
                );
            }
        }
        catch { }

        if (
            Array.isArray(
                state.historyMarkers
            )
        ) {
            state.historyMarkers.forEach(
                function (marker) {
                    try {
                        state.map.removeLayer(
                            marker
                        );
                    }
                    catch { }
                }
            );
        }

        try {
            if (
                state.historyStartMarker
            ) {
                state.map.removeLayer(
                    state.historyStartMarker
                );
            }
        }
        catch { }

        try {
            if (
                state.historyEndMarker
            ) {
                state.map.removeLayer(
                    state.historyEndMarker
                );
            }
        }
        catch { }

        state.historyRoute = null;
        state.historyMarkers = [];
        state.historyStartMarker = null;
        state.historyEndMarker = null;
    };

// ============================================================
// ADMIN HISTORY TIME FORMATTER
// ============================================================

window.formatAdminHistoryTime =
    function (value) {
        if (!value) return '-';

        try {
            const date =
                new Date(value);

            if (
                Number.isNaN(
                    date.getTime()
                )
            ) {
                return String(value);
            }

            return date.toLocaleString(
                'en-IN',
                {
                    day: '2-digit',
                    month: '2-digit',
                    year: 'numeric',
                    hour: '2-digit',
                    minute: '2-digit',
                    second: '2-digit'
                }
            );
        }
        catch {
            return String(value);
        }
    };

// ============================================================
// ADMIN MAP HELPERS
// ============================================================

window.getAdminLineMidpoint =
    function (a, b) {
        return [
            (
                Number(a[0]) +
                Number(b[0])
            ) / 2,
            (
                Number(a[1]) +
                Number(b[1])
            ) / 2
        ];
    };

window.formatAdminDistance =
    function (meters) {
        meters =
            Number(meters) || 0;

        return meters < 1000
            ? Math.round(meters) + ' m'
            : (
                meters / 1000
            ).toFixed(2) + ' km';
    };

window.escapeAdminHtml =
    function (value) {
        return String(
            value ?? ''
        ).replace(
            /[&<>"']/g,
            function (ch) {
                return {
                    '&': '&amp;',
                    '<': '&lt;',
                    '>': '&gt;',
                    '"': '&quot;',
                    "'": '&#039;'
                }[ch];
            }
        );
    };

// ============================================================
// ADMIN MAP DESTROY
// ============================================================

window.destroyAdminLiveStaffMap =
    function (mapId) {
        const state =
            window.adminLiveMaps?.[mapId];

        if (state?.markers) {
            Object.keys(state.markers).forEach(function (employeeId) {
                window.payrollCancelGeoAnimation?.(
                    'admin:' + mapId + ':' + employeeId);
            });
        }

        if (!state) return;

        try {
            window.clearAdminHistoryRoute(
                mapId
            );
        }
        catch { }

        try {
            state.map.remove();
        }
        catch { }

        delete window.adminLiveMaps[
            mapId
        ];
    };

// ============================================================
// ADMIN GPS HISTORY PLAYBACK
// FINAL REPLACEMENT VERSION
// ============================================================
//
// Features:
// - Session-specific playback
// - Play / Pause / Resume
// - Reset
// - Seek
// - Speed control
// - Start from selected point
// - Animated marker movement
// - Progressive route line
// - Current point popup
// - Playback state available to Blazor
// - Safe cleanup when map/session changes
// ============================================================

window.adminHistoryPlayback =
    window.adminHistoryPlayback || {};

window.startAdminHistoryPlayback =
    async function (
        mapId,
        history,
        employeeName,
        speed,
        startIndex
    ) {
        try {
            const state = window.adminLiveMaps?.[mapId];

            if (!state || !state.map || !Array.isArray(history) || history.length === 0) {
                return;
            }

            window.pauseAdminHistoryPlayback(mapId);
            window.stopAdminHistoryPlayback(mapId);

            const points = history
                .map(function (x, index) {
                    const latitude = Number(x.latitude ?? x.Latitude);
                    const longitude = Number(x.longitude ?? x.Longitude);

                    if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) {
                        return null;
                    }

                    const recordedRaw = x.recordedAtUtc ?? x.RecordedAtUtc;
                    const recordedAt = Date.parse(recordedRaw);

                    return {
                        index: index,
                        latitude: latitude,
                        longitude: longitude,
                        distance: Number(x.distanceFromOfficeMeters ?? x.DistanceFromOfficeMeters) || 0,
                        allowed: Number(x.allowedRadiusMeters ?? x.AllowedRadiusMeters) || 0,
                        within: Boolean(x.isWithinAllowedRadius ?? x.IsWithinAllowedRadius),
                        accuracy: Number(x.accuracyMeters ?? x.AccuracyMeters) || 0,
                        recordedAt: Number.isNaN(recordedAt) ? null : recordedAt,
                        recordedAtRaw: recordedRaw
                    };
                })
                .filter(Boolean)
                .sort(function (a, b) {
                    return (a.recordedAt || 0) - (b.recordedAt || 0);
                });

            if (points.length === 0) {
                return;
            }

            const requestedIndex = Number.isFinite(Number(startIndex))
                ? Number(startIndex)
                : (window.adminHistoryPlayback?.[mapId]?.index ?? 0);

            const initialIndex = Math.max(
                0,
                Math.min(points.length - 1, requestedIndex)
            );

            const playback = {
                mapId: mapId,
                points: points,
                employeeName: employeeName || "Employee",
                speed: Math.max(0.25, Number(speed) || 1),
                index: initialIndex,
                timer: null,
                animationFrame: null,
                marker: null,
                travelledLine: null,
                remainingLine: null,
                startMarker: null,
                endMarker: null,
                stopMarkers: [],
                eventMarkers: [],
                completed: initialIndex >= points.length - 1,
                lastTickTime: 0,
                followCamera: true
            };

            window.adminHistoryPlayback[mapId] = playback;

            const makeMarkerIcon = function (type, label) {
                const palette = {
                    start: { bg: "#16a34a", icon: "bi-play-fill" },
                    end: { bg: "#dc3545", icon: "bi-flag-fill" },
                    stop: { bg: "#f59e0b", icon: "bi-pause-fill" },
                    enter: { bg: "#16a34a", icon: "bi-box-arrow-in-right" },
                    exit: { bg: "#dc3545", icon: "bi-box-arrow-right" }
                };

                const p = palette[type] || palette.stop;

                return L.divIcon({
                    className: "payroll-playback-event-marker",
                    html:
                        '<span style="display:flex;align-items:center;justify-content:center;' +
                        'width:30px;height:30px;border-radius:50%;background:' + p.bg + ';' +
                        'color:#fff;border:2px solid rgba(255,255,255,.95);' +
                        'box-shadow:0 4px 14px rgba(0,0,0,.35);font-size:13px">' +
                        '<i class="bi ' + p.icon + '"></i></span>',
                    iconSize: [30, 30],
                    iconAnchor: [15, 15],
                    tooltipAnchor: [0, -14]
                });
            };

            const startPoint = points[0];
            const endPoint = points[points.length - 1];

            playback.startMarker = L.marker(
                [startPoint.latitude, startPoint.longitude],
                {
                    icon: makeMarkerIcon("start", "Start"),
                    zIndexOffset: 3000,
                    interactive: true
                }
            ).addTo(state.map);

            playback.startMarker.bindTooltip(
                "<strong>Journey Start</strong><br>" +
                window.escapeAdminHtml(
                    startPoint.recordedAt
                        ? new Date(startPoint.recordedAt).toLocaleString()
                        : "Recorded GPS start"
                ),
                {
                    direction: "top",
                    sticky: true,
                    className: "admin-playback-event-tooltip"
                }
            );

            if (points.length > 1) {
                playback.endMarker = L.marker(
                    [endPoint.latitude, endPoint.longitude],
                    {
                        icon: makeMarkerIcon("end", "End"),
                        zIndexOffset: 3000,
                        interactive: true
                    }
                ).addTo(state.map);

                playback.endMarker.bindTooltip(
                    "<strong>Journey End</strong><br>" +
                    window.escapeAdminHtml(
                        endPoint.recordedAt
                            ? new Date(endPoint.recordedAt).toLocaleString()
                            : "Last recorded GPS point"
                    ),
                    {
                        direction: "top",
                        sticky: true,
                        className: "admin-playback-event-tooltip"
                    }
                );
            }

            // Detect meaningful stops and geofence transitions from the real
            // recorded points. These are presentation-only markers.
            for (let i = 1; i < points.length; i++) {
                const previous = points[i - 1];
                const current = points[i];

                const distance = window.payrollHaversineMeters(
                    [previous.latitude, previous.longitude],
                    [current.latitude, current.longitude]
                );

                const gapSeconds =
                    previous.recordedAt && current.recordedAt
                        ? Math.max(0, (current.recordedAt - previous.recordedAt) / 1000)
                        : 0;

                if (distance <= 30 && gapSeconds >= 120) {
                    const stopMarker = L.marker(
                        [current.latitude, current.longitude],
                        {
                            icon: makeMarkerIcon("stop", "Stop"),
                            zIndexOffset: 2500
                        }
                    ).addTo(state.map);

                    stopMarker.bindTooltip(
                        "<strong>Stop</strong><br>" +
                        window.escapeAdminHtml(
                            Math.round(gapSeconds / 60) + " min"
                        ),
                        {
                            direction: "top",
                            sticky: true,
                            className: "admin-playback-event-tooltip"
                        }
                    );

                    playback.stopMarkers.push(stopMarker);
                }

                if (previous.within !== current.within) {
                    const eventType = current.within ? "enter" : "exit";
                    const eventMarker = L.marker(
                        [current.latitude, current.longitude],
                        {
                            icon: makeMarkerIcon(eventType, current.within ? "Enter" : "Exit"),
                            zIndexOffset: 2600
                        }
                    ).addTo(state.map);

                    eventMarker.bindTooltip(
                        "<strong>" +
                        (current.within ? "Entered radius" : "Left radius") +
                        "</strong><br>" +
                        (current.distance || 0).toFixed(0) +
                        " m from office",
                        {
                            direction: "top",
                            sticky: true,
                            className: "admin-playback-event-tooltip"
                        }
                    );

                    playback.eventMarkers.push(eventMarker);
                }
            }

            const icon = L.divIcon({
                className: "payroll-playback-marker",
                html:
                    '<div class="payroll-playback-avatar">' +
                    '<i class="bi bi-person-fill"></i>' +
                    '</div>',
                iconSize: [46, 46],
                iconAnchor: [23, 23]
            });

            const initialPoint = points[initialIndex];

            playback.marker = L.marker(
                [initialPoint.latitude, initialPoint.longitude],
                {
                    icon: icon,
                    zIndexOffset: 5000,
                    keyboard: false
                }
            ).addTo(state.map);

            playback.marker.bindTooltip(
                window.buildAdminPlaybackPopup(
                    playback.employeeName,
                    initialPoint,
                    initialIndex,
                    points.length
                ),
                {
                    direction: "top",
                    sticky: true,
                    className: "admin-playback-live-tooltip"
                }
            );

            playback.travelledLine = L.polyline(
                points.slice(0, initialIndex + 1).map(function (point) {
                    return [point.latitude, point.longitude];
                }),
                {
                    color: "#1688ff",
                    weight: 6,
                    opacity: 0.95,
                    lineJoin: "round",
                    lineCap: "round"
                }
            ).addTo(state.map);

            playback.remainingLine = L.polyline(
                points.slice(initialIndex).map(function (point) {
                    return [point.latitude, point.longitude];
                }),
                {
                    color: "#94a3b8",
                    weight: 4,
                    opacity: 0.55,
                    dashArray: "7,8",
                    lineJoin: "round",
                    lineCap: "round"
                }
            ).addTo(state.map);

            window.moveAdminPlaybackMarker(playback, initialIndex);

            try {
                state.map.invalidateSize(true);
            } catch { }

            state.map.panTo(
                [initialPoint.latitude, initialPoint.longitude],
                { animate: false }
            );

            if (initialIndex < points.length - 1) {
                window.resumeAdminHistoryPlayback(mapId);
            }
        }
        catch (error) {
            console.error("Admin history playback error:", error);
        }
    };


window.resumeAdminHistoryPlayback =
    function (mapId) {
        const playback = window.adminHistoryPlayback?.[mapId];

        if (!playback || !playback.marker || playback.points.length === 0) {
            return;
        }

        window.pauseAdminHistoryPlayback(mapId);

        if (playback.index >= playback.points.length - 1) {
            playback.completed = true;
            return;
        }

        playback.completed = false;

        const animateSegment = function (fromIndex) {
            const current = window.adminHistoryPlayback?.[mapId];
            if (!current || current.index !== fromIndex) return;

            const from = current.points[fromIndex];
            const to = current.points[fromIndex + 1];

            const fromLatLng = [from.latitude, from.longitude];
            const toLatLng = [to.latitude, to.longitude];

            let durationMs = 1200;

            if (from.recordedAt && to.recordedAt) {
                const realGap = Math.max(250, to.recordedAt - from.recordedAt);
                // Compress long real-world gaps while preserving their order.
                const compressed = Math.min(5000, Math.max(700, realGap));
                durationMs = compressed / Math.max(0.25, current.speed);
            } else {
                durationMs = 1200 / Math.max(0.25, current.speed);
            }

            durationMs = Math.max(120, Math.min(5000, durationMs));

            const started = performance.now();

            const frame = function (now) {
                const live = window.adminHistoryPlayback?.[mapId];

                if (!live || live.index !== fromIndex) {
                    if (live) live.animationFrame = null;
                    return;
                }

                const progress = Math.min(
                    1,
                    Math.max(0, (now - started) / durationMs)
                );

                // Smooth ease-in-out motion.
                const eased = progress < .5
                    ? 2 * progress * progress
                    : 1 - Math.pow(-2 * progress + 2, 2) / 2;

                const lat =
                    from.latitude +
                    (to.latitude - from.latitude) * eased;

                const lng =
                    from.longitude +
                    (to.longitude - from.longitude) * eased;

                const position = [lat, lng];

                live.marker.setLatLng(position);

                if (live.travelledLine) {
                    const travelled = live.points
                        .slice(0, fromIndex + 1)
                        .map(function (point) {
                            return [point.latitude, point.longitude];
                        });

                    travelled.push(position);
                    live.travelledLine.setLatLngs(travelled);
                }

                if (live.remainingLine) {
                    const remaining = [position].concat(
                        live.points
                            .slice(fromIndex + 1)
                            .map(function (point) {
                                return [point.latitude, point.longitude];
                            })
                    );

                    live.remainingLine.setLatLngs(remaining);
                }

                if (live.followCamera) {
                    try {
                        const map = window.adminLiveMaps?.[mapId]?.map;
                        if (map) {
                            const center = map.getCenter();
                            const delta =
                                Math.abs(center.lat - lat) +
                                Math.abs(center.lng - lng);

                            if (delta > 0.001) {
                                map.panTo(position, {
                                    animate: true,
                                    duration: Math.min(.35, durationMs / 1000)
                                });
                            }
                        }
                    } catch { }
                }

                if (live.marker.getTooltip()) {
                    const point = to;
                    live.marker.setTooltipContent(
                        window.buildAdminPlaybackPopup(
                            live.employeeName,
                            point,
                            fromIndex + (progress >= .5 ? 1 : 0),
                            live.points.length
                        )
                    );
                }

                if (progress < 1) {
                    live.animationFrame =
                        requestAnimationFrame(frame);
                    return;
                }

                live.animationFrame = null;
                live.index = fromIndex + 1;

                if (live.index >= live.points.length - 1) {
                    live.completed = true;
                    window.moveAdminPlaybackMarker(live, live.index);
                    return;
                }

                animateSegment(live.index);
            };

            current.animationFrame =
                requestAnimationFrame(frame);
        };

        animateSegment(playback.index);
    };


window.pauseAdminHistoryPlayback =
    function (mapId) {

        const playback =
            window.adminHistoryPlayback?.[mapId];

        if (!playback) {
            return;
        }

        if (playback.timer) {

            clearInterval(
                playback.timer
            );

            playback.timer =
                null;
        }

        if (
            playback.animationFrame
        ) {

            cancelAnimationFrame(
                playback.animationFrame
            );

            playback.animationFrame =
                null;
        }
    };


window.resetAdminHistoryPlayback =
    function (mapId) {

        const playback =
            window.adminHistoryPlayback?.[mapId];

        if (!playback) {
            return;
        }

        window.pauseAdminHistoryPlayback(
            mapId
        );

        playback.index =
            0;

        playback.completed =
            false;

        window.moveAdminPlaybackMarker(
            playback,
            0
        );
    };


window.stopAdminHistoryPlayback =
    function (mapId) {
        const playback = window.adminHistoryPlayback?.[mapId];

        if (!playback) {
            return;
        }

        window.pauseAdminHistoryPlayback(mapId);

        try {
            const state = window.adminLiveMaps?.[mapId];

            if (state?.map) {
                [
                    playback.marker,
                    playback.travelledLine,
                    playback.remainingLine,
                    playback.startMarker,
                    playback.endMarker
                ].forEach(function (layer) {
                    if (layer) {
                        try { state.map.removeLayer(layer); } catch { }
                    }
                });

                (playback.stopMarkers || []).forEach(function (layer) {
                    try { state.map.removeLayer(layer); } catch { }
                });

                (playback.eventMarkers || []).forEach(function (layer) {
                    try { state.map.removeLayer(layer); } catch { }
                });
            }
        }
        catch {
        }

        delete window.adminHistoryPlayback[mapId];
    };


window.restoreAdminHistoryPlaybackLayout =
    function (mapId) {
        try {
            const state = window.adminLiveMaps?.[mapId];
            if (!state?.map) return;

            state.map.invalidateSize(true);

            setTimeout(function () {
                try { state.map.invalidateSize(true); } catch { }
            }, 100);

            setTimeout(function () {
                try { state.map.invalidateSize(true); } catch { }
            }, 350);
        }
        catch {
        }
    };


window.seekAdminHistoryPlayback =
    function (
        mapId,
        index
    ) {

        const playback =
            window.adminHistoryPlayback?.[mapId];

        if (!playback) {
            return;
        }

        const target =
            Math.max(
                0,
                Math.min(
                    playback.points.length - 1,
                    Number(index) || 0
                )
            );

        playback.index =
            target;

        playback.completed =
            target >=
            playback.points.length - 1;

        window.moveAdminPlaybackMarker(
            playback,
            target
        );
    };


window.moveAdminPlaybackMarker =
    function (
        playback,
        index
    ) {

        if (
            !playback ||
            !playback.marker
        ) {
            return;
        }

        const point =
            playback.points[index];

        if (!point) {
            return;
        }

        const position = [
            point.latitude,
            point.longitude
        ];

        playback.marker.setLatLng(
            position
        );

        playback.marker.setPopupContent(
            window.buildAdminPlaybackPopup(
                playback.employeeName,
                point,
                index,
                playback.points.length
            )
        );

        /*
         * Update progressive playback route.
         */
        if (
            playback.routeLine
        ) {

            playback.routeLine.setLatLngs(
                playback.points
                    .slice(
                        0,
                        index + 1
                    )
                    .map(function (item) {
                        return [
                            item.latitude,
                            item.longitude
                        ];
                    })
            );
        }

        const state =
            window.adminLiveMaps?.[
            playback.mapId
            ];

        if (
            state?.map
        ) {

            /*
             * Do not open a popup every timer tick.
             * The popup is opened when the marker is clicked
             * or when playback starts.
             */
            if (
                index === 0 ||
                index ===
                playback.points.length - 1
            ) {
                playback.marker.openPopup();
            }

            state.map.panTo(
                position,
                {
                    animate:
                        true,

                    duration:
                        0.35
                }
            );
        }
    };


window.buildAdminPlaybackPopup =
    function (
        employeeName,
        point,
        index,
        total
    ) {

        const safeName =
            window.escapeAdminHtml(
                employeeName
            );

        const time =
            window.formatAdminHistoryTime(
                point.recordedAt
            );

        const distance =
            window.formatAdminDistance(
                point.distance
            );

        const allowed =
            point.allowed > 0
                ? point.allowed + " m"
                : "-";

        const accuracy =
            point.accuracy > 0
                ? Math.round(
                    point.accuracy
                ) + " m"
                : "-";

        const status =
            point.within
                ? "Within allowed range"
                : "Outside allowed range";

        const statusColor =
            point.within
                ? "#198754"
                : "#dc3545";

        return (
            '<div style="min-width:230px">' +

            "<strong>" +
            safeName +
            "</strong>" +

            '<hr style="margin:6px 0">' +

            "<strong>GPS Point " +
            (index + 1) +
            " / " +
            total +
            "</strong><br>" +

            "<span>Time: " +
            time +
            "</span><br>" +

            "<span>Distance: " +
            distance +
            "</span><br>" +

            "<span>Allowed: " +
            allowed +
            "</span><br>" +

            "<span>Accuracy: " +
            accuracy +
            "</span><br>" +

            "<span>Latitude: " +
            point.latitude.toFixed(6) +
            "</span><br>" +

            "<span>Longitude: " +
            point.longitude.toFixed(6) +
            "</span><br>" +

            '<strong style="color:' +
            statusColor +
            '">' +
            status +
            "</strong>" +

            "</div>"
        );
    };


window.setAdminHistoryPlaybackSpeed =
    function (
        mapId,
        speed
    ) {

        const playback =
            window.adminHistoryPlayback?.[
            mapId
            ];

        if (!playback) {
            return;
        }

        const wasPlaying =
            !!playback.timer;

        playback.speed =
            Math.max(
                0.25,
                Number(speed) || 1
            );

        window.pauseAdminHistoryPlayback(
            mapId
        );

        if (
            wasPlaying &&
            playback.index <
            playback.points.length - 1
        ) {
            window.resumeAdminHistoryPlayback(
                mapId
            );
        }
    };


window.getAdminHistoryPlaybackState =
    function (mapId) {

        const playback =
            window.adminHistoryPlayback?.[
            mapId
            ];

        if (!playback) {
            return null;
        }

        return {
            index:
                playback.index,

            total:
                playback.points.length,

            playing:
                !!playback.timer,

            completed:
                !!playback.completed,

            speed:
                playback.speed
        };
    };