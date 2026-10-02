// ============================================================
// Payroll.Web - Shared Theme / Browser Interop
// ============================================================

window.payrollEscapeHtml = function (value) {
    return String(value ?? '').replace(/[&<>"']/g, function (ch) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[ch];
    });
};

// ============================================================
// DOCUMENT TITLE
// ============================================================

// MainLayout invokes this helper during the first render and on navigation.
// Keep it deliberately conservative so HeadOutlet/PageTitle remains the
// authoritative title when a page provides one. The fallback prevents a
// missing JS function from generating a Blazor JSInterop exception.
window.payrollDocumentTitle = function (locationUrl) {
    try {
        if (!locationUrl) return document.title;
        var pathname = '';
        try {
            var url = new URL(locationUrl, window.location.origin);
            pathname = url.pathname.toLowerCase().replace(/^\/|\/$/g, '');
        } catch (_) {
            pathname = String(locationUrl || '').toLowerCase().replace(/^[a-z]+:\/\/[^/]+/i, '').replace(/^\/|\/$/g, '');
        }

        var routeTitles = {
            '': 'Admin Dashboard',
            'home': 'Admin Dashboard',
            'admin': 'Admin Dashboard',
            'bonus-management': 'Bonus Management',
            'attendancelogs': 'Attendance Master Logs',
            'company-attendance-report': 'Company Attendance Report',
            'manual-punch-correction': 'Manual Punch Correction',
            'punch-approvals': 'Punch Correction Approval',
            'attendance-event-monitoring': 'Attendance Event Monitoring',
            'runpayroll': 'Run Payroll',
            'payslip': 'Payslip',
            'salary-advances': 'Salary Advances',
            'leave-management': 'Leave Management',
            'scheduling': 'Shift Scheduler',
            'settings/company': 'Company Settings',
            'settings/features': 'Feature & Permission Manager',
            'settings/holidays': 'Holiday Management',
            'superadmin/tenants': 'SuperAdmin Command Center',
            'user-management': 'User Management',
            'audit-logs': 'Audit Logs',
            'recycle-bin': 'Recycle Bin',
            'location-history': 'Location Tracking History',
            'location-stays': 'Location Stays',
            'offline-tracking': 'Offline GPS Tracking',
            'report-center': 'Report Center',
            'exit-management': 'Exit Management',
            'fbp-components': 'FBP Component Management',
            'fbp-approvals': 'FBP Declaration Approval',
            'tax-declarations': 'Manage Tax Declarations',
            'regularization-approvals': 'Regularization Approval',
            'year-end-summary': 'Year-End Summary',
            'employee-home': 'Employee Home',
            'my-profile': 'Employee Profile',
            'employee/profile': 'Employee Profile',
            'identity/account/manage': 'Employee Profile',
            'identity/account/manage/index': 'Employee Profile',
            'my-payslips': 'My Payslips',
            'my-attendance': 'My Attendance',
            'my-leave-request': 'Request Leave',
            'my-leave-history': 'My Leave History',
            'my-shifts': 'My Shift Schedule',
            'my-salary-advances': 'My Salary Advances',
            'my-tax-declaration': 'My Tax Declaration',
            'my-bonuses': 'My Bonuses',
            'my-resignation': 'My Resignation',
            'my-regularization': 'Punch Regularization',
            'my-reports': 'My Personal Reports',
            'my-fbp-declaration': 'My FBP Declaration',
            'employees': 'Employee Management'
        };

        if (routeTitles[pathname]) {
            document.title = routeTitles[pathname] + ' - BioMetric + Payroll';
        } else if (pathname.startsWith('employees/details')) {
            document.title = 'Employee Details - BioMetric + Payroll';
        } else if (pathname.startsWith('employees/insights')) {
            document.title = 'Employee Insights - BioMetric + Payroll';
        } else if (pathname.startsWith('offline-tracking/')) {
            document.title = 'Offline Tracking Details - BioMetric + Payroll';
        }
        return document.title;
    } catch (e) {
        console.warn('Unable to set document title', e);
        return document.title || 'Payroll.Web';
    }
};

// ============================================================
// THEME
// ============================================================

// Theme is persisted per authenticated user on the server.
// Browser storage is only a small same-user/tab cache and is namespaced
// by the authenticated user's stable key so one user can never inherit
// another user's theme on the same browser.
window.themeInterop = {
    _activeUserKey: null,

    _themeStorageKey: function (userKey) {
        var key = String(userKey || '').trim().toLowerCase();

        // Never use one shared browser key for authenticated users.
        // A missing user key means there is no user-specific cache.
        return key ? 'payroll_theme_' + key : null;
    },

    setActiveUser: function (userKey) {
        var key = String(userKey || '').trim();
        this._activeUserKey = key || null;
        return this._activeUserKey;
    },

    setThemeOnBody: function (theme) {
        var normalized = theme === 'dark' ? 'dark' : 'light';
        document.body.classList.toggle('dark', normalized === 'dark');
        document.documentElement.setAttribute('data-theme', normalized);
        document.body.setAttribute('data-theme', normalized);
        document.documentElement.setAttribute('data-bs-theme', normalized);
        document.body.setAttribute('data-bs-theme', normalized);
    },

    saveTheme: function (theme, userKey) {
        var normalized = theme === 'dark' ? 'dark' : 'light';
        var storageKey = this._themeStorageKey(userKey);

        if (storageKey) {
            try {
                localStorage.setItem(storageKey, normalized);
            } catch (e) {
                console.warn('Unable to save user theme cache to localStorage', e);
            }
        }

        this.setThemeOnBody(normalized);
        return normalized;
    },

    loadTheme: function (userKey) {
        var storageKey = this._themeStorageKey(userKey);

        if (!storageKey) {
            return 'light';
        }

        try {
            return localStorage.getItem(storageKey) || 'light';
        } catch (e) {
            console.warn('Unable to read user theme cache from localStorage', e);
            return 'light';
        }
    },

    applySavedTheme: function (userKey) {
        var theme = this.loadTheme(userKey);
        this.setThemeOnBody(theme);
        return theme;
    }
};

// Keep already-open tabs for the same authenticated user synchronized.
// MainLayout passes the user-specific key when saving the preference.
try {
    window.addEventListener('storage', function (event) {
        if (!event.key || event.key.indexOf('payroll_theme_') !== 0) return;
        if (event.newValue !== 'dark' && event.newValue !== 'light') return;

        var interop = window.themeInterop;
        if (!interop ||
            typeof interop.setThemeOnBody !== 'function') {
            return;
        }

        // Storage events are origin-wide. Only the authenticated user's
        // namespace may change the theme of this tab.
        var activeKey = interop._activeUserKey
            ? interop._themeStorageKey(interop._activeUserKey)
            : null;

        if (!activeKey || event.key !== activeKey) return;

        interop.setThemeOnBody(event.newValue);
    });
} catch (e) { }

// ============================================================
// GEOLOCATION - ROBUST CURRENT POSITION
// ============================================================

window.getCoords = async function () {

    if (!navigator.geolocation) {
        throw new Error(
            "Geolocation is not supported by this browser."
        );
    }

    // Fast path: if persistent watcher already has a recent location, return it immediately
    if (window.EmployeeGpsTracker && typeof window.EmployeeGpsTracker.getLatestLocation === 'function') {
        try {
            const cached = window.EmployeeGpsTracker.getLatestLocation(300000);
            if (cached && Number.isFinite(cached.Latitude) && Number.isFinite(cached.Longitude)) {
                return cached;
            }
        } catch { }
    }
    if (window.persistentEmployeeGps &&
        Number.isFinite(window.persistentEmployeeGps.lastLatitude) &&
        Number.isFinite(window.persistentEmployeeGps.lastLongitude)) {
        return {
            Latitude: window.persistentEmployeeGps.lastLatitude,
            Longitude: window.persistentEmployeeGps.lastLongitude,
            Accuracy: 30
        };
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
     * First try a recent location with reasonable timeout.
     */
    try {

        const position = await getPosition({
            enableHighAccuracy: false,
            timeout: 5000,
            maximumAge: 30000
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
     * Then request a fresh high-accuracy position with reasonable timeout.
     */
    try {

        const position = await getPosition({
            enableHighAccuracy: true,
            timeout: 6000,
            maximumAge: 10000
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

        // Fallback to any persistent fix before throwing
        if (window.persistentEmployeeGps &&
            Number.isFinite(window.persistentEmployeeGps.lastLatitude) &&
            Number.isFinite(window.persistentEmployeeGps.lastLongitude)) {
            return {
                Latitude: window.persistentEmployeeGps.lastLatitude,
                Longitude: window.persistentEmployeeGps.lastLongitude,
                Accuracy: 50
            };
        }

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

window.payrollSmoothMoveMarker =
    function (marker, key, target, durationMs, onFrame) {
        if (!marker || !Array.isArray(target) || target.length < 2) {
            return;
        }

        window.payrollGeoAnimationState = window.payrollGeoAnimationState || {};
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
            !Number.isFinite(end[1]) ||
            (start[0] === 0 && start[1] === 0)
        ) {
            marker.setLatLng(end);
            if (typeof onFrame === 'function') {
                onFrame(end);
            }
            return;
        }

        // LARGE-JUMP SNAP: If the marker needs to move > 300 meters (e.g. employee traveled in
        // vehicle, or session reconnected after gap), snap directly to the destination.
        // Never slowly crawl across 5 km of town through lakes and buildings.
        const jumpDistanceMeters = window.payrollHaversineMeters(start, end);
        if (jumpDistanceMeters > 300) {
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
                62000,
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

window.payrollEnsureAdminTooltipVisibility = function () {
    try {
        const styleId = 'payroll-admin-tooltip-visibility-fix';
        if (document.getElementById(styleId)) return;
        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = `
            .admin-live-hover-tooltip,
            .admin-live-hover-tooltip .leaflet-popup-content-wrapper,
            .admin-live-hover-tooltip .leaflet-popup-content,
            .admin-office-tooltip {
                visibility: visible !important;
                opacity: 1 !important;
                overflow: visible !important;
                z-index: 10000 !important;
            }
            .admin-live-hover-card {
                display: block !important;
                visibility: visible !important;
                opacity: 1 !important;
                pointer-events: none !important;
            }
        `;
        document.head.appendChild(style);
    } catch (_) { }
};

window.payrollKeepAdminTooltipVisible = function (map, marker) {
    if (!map || !marker || marker._payrollTooltipVisibilityBound) return;
    marker._payrollTooltipVisibilityBound = true;

    marker.on('tooltipopen', function (event) {
        try {
            window.requestAnimationFrame(function () {
                try {
                    var tooltip = event && event.tooltip;
                    var element = tooltip && tooltip.getElement
                        ? tooltip.getElement()
                        : null;
                    var mapElement = map.getContainer();

                    if (!element || !mapElement) return;

                    var rect = element.getBoundingClientRect();
                    var mapRect = mapElement.getBoundingClientRect();
                    var padding = 12;
                    var dx = 0;
                    var dy = 0;

                    if (rect.left < mapRect.left + padding) {
                        dx = rect.left - (mapRect.left + padding);
                    } else if (rect.right > mapRect.right - padding) {
                        dx = rect.right - (mapRect.right - padding);
                    }

                    if (rect.top < mapRect.top + padding) {
                        dy = rect.top - (mapRect.top + padding);
                    } else if (rect.bottom > mapRect.bottom - padding) {
                        dy = rect.bottom - (mapRect.bottom - padding);
                    }

                    if (Math.abs(dx) > 1 || Math.abs(dy) > 1) {
                        map.panBy([dx, dy], {
                            animate: true,
                            duration: 0.22,
                            noMoveStart: true
                        });
                    }
                } catch (_) { }
            });
        } catch (_) { }
    });
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
    const base = String(window.payrollRoutingServiceUrl || 'https://router.project-osrm.org').replace(/\/$/, '');
    if (!base) return null;
    const url = `${base}/route/v1/driving/${a[1]},${a[0]};${b[1]},${b[0]}?overview=full&geometries=geojson&steps=true&annotations=false`;
    try {
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
    } catch (err) {
        if (err?.name !== 'AbortError') console.warn('Road route fetch failed:', err);
        return null;
    }
};

window.payrollUpdateRouteEmployeeEndpoint = function (polyline, employeePosition) {
    if (!polyline) return;
    try {
        const current = polyline.getLatLngs();
        if (!Array.isArray(current) || current.length < 2) return;
        const targetLat = Number(Array.isArray(employeePosition) ? employeePosition[0] : employeePosition?.lat);
        const targetLng = Number(Array.isArray(employeePosition) ? employeePosition[1] : employeePosition?.lng);
        if (!Number.isFinite(targetLat) || !Number.isFinite(targetLng)) return;

        const getCoord = function (pt) {
            if (!pt) return [0, 0];
            const lat = Number(pt.lat !== undefined ? pt.lat : (Array.isArray(pt) ? pt[0] : 0));
            const lng = Number(pt.lng !== undefined ? pt.lng : (Array.isArray(pt) ? pt[1] : 0));
            return [lat, lng];
        };

        const pStart = getCoord(current[0]);
        const pEnd = getCoord(current[current.length - 1]);
        const dStart = window.payrollHaversineMeters(pStart, [targetLat, targetLng]);
        const dEnd = window.payrollHaversineMeters(pEnd, [targetLat, targetLng]);

        // Road route starts at employee (current[0]) and terminates at office (current[last]).
        // Always snap the employee side of the route to the marker motion.
        // Under no circumstances overwrite the office endpoint with the employee coordinate,
        // which creates a harsh diagonal crossing chord across the city blocks.
        if (dStart <= dEnd) {
            current[0] = L.latLng(targetLat, targetLng);
        } else if (dEnd < 1000) {
            current[current.length - 1] = L.latLng(targetLat, targetLng);
        }
        polyline.setLatLngs(current);
    } catch { }
};

window.payrollFetchMultiPointRoadRoute = async function (rawPoints, options = {}) {
    if (!Array.isArray(rawPoints) || rawPoints.length < 2) return null;
    const valid = rawPoints
        .map(p => [Number(p[0] ?? p.latitude ?? p.lat), Number(p[1] ?? p.longitude ?? p.lng ?? p.lon)])
        .filter(p => Number.isFinite(p[0]) && Number.isFinite(p[1]));
    if (valid.length < 2) return null;

    // Filter outlier spikes (jumps > 1000m between consecutive samples)
    const cleaned = [valid[0]];
    for (let i = 1; i < valid.length; i++) {
        const d = window.payrollHaversineMeters(cleaned[cleaned.length - 1], valid[i]);
        if (d > 0.5 && d < 2000) {
            cleaned.push(valid[i]);
        }
    }
    if (cleaned.length < 2) cleaned.push(valid[valid.length - 1]);

    // Downsample for OSRM URL limits (min 20m apart, max 30 points)
    const waypoints = [cleaned[0]];
    for (let i = 1; i < cleaned.length - 1; i++) {
        const d = window.payrollHaversineMeters(waypoints[waypoints.length - 1], cleaned[i]);
        if (d >= 25) waypoints.push(cleaned[i]);
    }
    waypoints.push(cleaned[cleaned.length - 1]);
    const sampled = waypoints.length > 30
        ? waypoints.filter((_, idx) => idx === 0 || idx === waypoints.length - 1 || idx % Math.ceil(waypoints.length / 28) === 0)
        : waypoints;

    const base = String(window.payrollRoutingServiceUrl || 'https://router.project-osrm.org').replace(/\/$/, '');
    const coordString = sampled.map(p => `${p[1].toFixed(6)},${p[0].toFixed(6)}`).join(';');
    const url = `${base}/route/v1/driving/${coordString}?overview=full&geometries=geojson&steps=false&annotations=false`;

    try {
        const response = await fetch(url, { method: 'GET', mode: 'cors', cache: 'no-store', signal: options.controller?.signal });
        if (response.ok) {
            const data = await response.json();
            if (data.code === 'Ok' && data.routes?.[0]?.geometry?.coordinates) {
                const geom = data.routes[0].geometry.coordinates
                    .filter(c => Array.isArray(c) && c.length >= 2)
                    .map(c => [Number(c[1]), Number(c[0])]);
                if (geom.length >= 2) {
                    return {
                        distanceMeters: Number(data.routes[0].distance) || 0,
                        durationSeconds: Number(data.routes[0].duration) || 0,
                        geometry: geom,
                        isSnapped: true
                    };
                }
            }
        }
    } catch (_) { }

    // Fallback: smooth spline interpolation so path curves organically instead of harsh straight-line chords
    return {
        geometry: window.payrollGenerateSmoothSpline(cleaned),
        isSnapped: false
    };
};

window.payrollGenerateSmoothSpline = function (points, pointsPerSegment = 5) {
    if (!Array.isArray(points) || points.length < 2) return points || [];
    if (points.length === 2) return points;
    const result = [];
    for (let i = 0; i < points.length - 1; i++) {
        const p0 = i > 0 ? points[i - 1] : points[i];
        const p1 = points[i];
        const p2 = points[i + 1];
        const p3 = i < points.length - 2 ? points[i + 2] : p2;
        for (let step = 0; step < pointsPerSegment; step++) {
            const t = step / pointsPerSegment;
            const t2 = t * t;
            const t3 = t2 * t;
            const lat = 0.5 * ((2 * p1[0]) + (-p0[0] + p2[0]) * t + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2 + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3);
            const lng = 0.5 * ((2 * p1[1]) + (-p0[1] + p2[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2 + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3);
            result.push([lat, lng]);
        }
    }
    result.push(points[points.length - 1]);
    return result;
};

window.payrollReverseGeocodeLocation = async function (lat, lon) {
    const nLat = Number(lat);
    const nLon = Number(lon);
    if (!Number.isFinite(nLat) || !Number.isFinite(nLon)) return null;
    window.__payrollReverseGeocodeCache = window.__payrollReverseGeocodeCache || {};
    const key = nLat.toFixed(4) + ',' + nLon.toFixed(4);
    if (window.__payrollReverseGeocodeCache[key]) {
        return window.__payrollReverseGeocodeCache[key];
    }

    // 1. Try Komoot Photon first for high-resolution local villages/suburbs/POIs (e.g. Kurumbapet)
    try {
        const photonUrl = 'https://photon.komoot.io/reverse?lat=' + encodeURIComponent(nLat) + '&lon=' + encodeURIComponent(nLon);
        const pRes = await fetch(photonUrl, { headers: { 'Accept': 'application/json' }, mode: 'cors' });
        if (pRes.ok) {
            const pData = await pRes.json();
            const feature = pData?.features?.[0];
            const p = feature?.properties;
            if (p) {
                let locality = p.locality || p.district || p.suburb || '';
                let placeName = String(p.name || '').trim();

                // If POI name contains comma (e.g. "Sub Centre, Kurumbapet")
                if (placeName.includes(',')) {
                    const subParts = placeName.split(',').map(s => s.trim()).filter(Boolean);
                    if (subParts.length >= 2) {
                        const candidateLocality = subParts[subParts.length - 1];
                        if (!locality && candidateLocality) {
                            locality = candidateLocality;
                            placeName = subParts[0];
                        }
                    }
                } else if (!locality && placeName && !/road|street|nagar|colony|lane|avenue|salai/i.test(placeName)) {
                    locality = placeName;
                    placeName = '';
                }

                const street = p.street || (p.osm_key === 'highway' ? p.name : '');
                const city = p.city || p.town || p.county || '';

                const parts = [
                    locality,
                    street && street !== locality ? street : null,
                    city && city !== locality ? city : null
                ].filter(Boolean);

                if (!locality && placeName && !parts.includes(placeName)) {
                    parts.unshift(placeName);
                }

                const unique = [...new Set(parts.map(String).map(s => s.trim()))].filter(Boolean);
                if (unique.length) {
                    const addr = unique.join(', ');
                    window.__payrollReverseGeocodeCache[key] = addr;
                    return addr;
                }
            }
        }
    } catch (_) { }

    const renderAddress = function (data) {
        const a = data?.address || {};
        const admin = data?.localityInfo?.administrative || [];
        const road = a.road || a.pedestrian || a.street || a.highway || a.residential || '';
        const locality = a.suburb || a.quarter || a.neighbourhood || a.village || a.hamlet || data?.locality || admin.find?.(x => /taluk|district|subdivision/i.test(x?.description || ''))?.name || '';
        const city = a.city || a.town || a.county || a.state_district || '';
        const parts = [
            road,
            locality && locality !== road ? locality : null,
            city && city !== locality && city !== road ? city : null
        ].filter(Boolean);
        const unique = [...new Set(parts.map(String).map(s => s.trim()))].filter(Boolean);
        return unique.length ? unique.join(', ') : (data?.display_name || '');
    };

    // 2. Fallback to OpenStreetMap Nominatim
    try {
        const reverseUrl = 'https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat=' + encodeURIComponent(nLat) + '&lon=' + encodeURIComponent(nLon) + '&zoom=18&addressdetails=1';
        const res = await fetch(reverseUrl, { headers: { 'Accept': 'application/json' }, mode: 'cors' });
        if (res.ok) {
            const data = await res.json();
            const addr = renderAddress(data);
            if (addr) {
                window.__payrollReverseGeocodeCache[key] = addr;
                return addr;
            }
        }
    } catch (_) { }

    // 3. Fallback to BigDataCloud
    try {
        const fallbackUrl = 'https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=' + encodeURIComponent(nLat) + '&longitude=' + encodeURIComponent(nLon) + '&localityLanguage=en';
        const res = await fetch(fallbackUrl, { headers: { 'Accept': 'application/json' }, mode: 'cors' });
        if (res.ok) {
            const data = await res.json();
            const addr = renderAddress(data);
            if (addr) {
                window.__payrollReverseGeocodeCache[key] = addr;
                return addr;
            }
        }
    } catch (_) { }

    const fallbackCoord = nLat.toFixed(5) + ', ' + nLon.toFixed(5);
    window.__payrollReverseGeocodeCache[key] = fallbackCoord;
    return fallbackCoord;
};

window.payrollMakeAdminMarkerTooltipHtml = function (metaInitials, safeDisplayName, rangeClass, rangeLabel, safeRole, speedEmoji, speedLabel, address) {
    const safeAddress = address ? (window.escapeAdminHtml ? window.escapeAdminHtml(address) : address) : '';
    const addressBlock = safeAddress
        ? '<div style="font-size:11px;color:#cbd5e1;margin-top:5px;line-height:1.3;display:flex;align-items:flex-start;gap:4px;border-top:1px solid rgba(255,255,255,0.12);padding-top:4px;">' +
          '<span>📍</span> <span>' + safeAddress + '</span>' +
          '</div>'
        : '';

    return '<div class="admin-live-hover-card" style="min-width:170px;max-width:270px;">' +
           '<div class="admin-live-hover-title">' +
           '<span class="hover-avatar">' + (metaInitials || '?').toUpperCase() + '</span>' +
           '<strong>' + safeDisplayName + '</strong>' +
           '<span class="hover-state ' + rangeClass + '">' + rangeLabel + '</span>' +
           '</div>' +
           '<div style="font-size:11px;color:#b0bec5;margin-top:4px;">' +
           '🏷️ ' + safeRole + '&nbsp;&nbsp;' + speedEmoji + ' ' + speedLabel +
           '</div>' +
           addressBlock +
           '</div>';
};

window.payrollFetchMarkerAddress = function (marker, lat, lng, onUpdate) {
    if (!marker || !Number.isFinite(lat) || !Number.isFinite(lng)) return;
    const moved = marker._lastGeocodedPos
        ? (window.payrollHaversineMeters ? window.payrollHaversineMeters(marker._lastGeocodedPos, [lat, lng]) : 999)
        : Infinity;
    if (marker._currentAddress && moved < 40) {
        if (typeof onUpdate === 'function') onUpdate(marker._currentAddress);
        return;
    }
    marker._lastGeocodedPos = [lat, lng];
    if (typeof window.payrollReverseGeocodeLocation === 'function') {
        window.payrollReverseGeocodeLocation(lat, lng).then(function (addr) {
            if (addr) {
                marker._currentAddress = addr;
                if (typeof onUpdate === 'function') onUpdate(addr);
            }
        }).catch(function () { });
    }
};

window.payrollGetNextRoadName = function (route) {
    const step = (route?.steps || []).find(s => String(s?.name || s?.ref || '').trim());
    if (!step) return 'Road route';
    const name = String(step.name || '').trim();
    const ref = String(step.ref || '').trim();
    if (name && ref && ref !== name) return `${name} (${ref})`;
    return name || ref || 'Road route';
};

window.payrollUpdateEmployeeAddress = function (mapData, coords) {
    if (!mapData || !coords || !Number.isFinite(Number(coords[0])) || !Number.isFinite(Number(coords[1]))) return;
    const lat = Number(coords[0]);
    const lon = Number(coords[1]);
    const moved = mapData.lastGeocodedPosition
        ? window.payrollHaversineMeters(mapData.lastGeocodedPosition, [lat, lon])
        : Infinity;

    if (mapData.isGeocoding) return;
    if (mapData.currentAddress && moved < 40) return;

    mapData.isGeocoding = true;
    mapData.lastGeocodedPosition = [lat, lon];
    window.payrollReverseGeocodeLocation(lat, lon).then(function (addr) {
        mapData.isGeocoding = false;
        if (addr) {
            mapData.currentAddress = addr;
            window.payrollRefreshEmployeeJourneyOverlay(mapData);
        }
    }).catch(function () {
        mapData.isGeocoding = false;
    });
};

window.payrollRefreshEmployeeJourneyOverlay = function (mapData, routeOverride) {
    if (!mapData || !mapData.journeyOverlay) return;
    const user = mapData.userPosition || mapData.lastRawPosition;
    const office = mapData.office;
    if (!user || !office) return;

    const route = routeOverride !== undefined ? routeOverride : mapData.routeState?.route;
    const airDist = window.payrollHaversineMeters(user, office);
    const distanceMeters = (route && Number(route.distanceMeters) > 0) ? Number(route.distanceMeters) : airDist;
    const isArrived = distanceMeters <= 35;
    const allowedRadius = Math.max(35, Number(mapData.radius) || 100);
    const isWithin = distanceMeters <= allowedRadius;

    const durationSeconds = (route && Number(route.durationSeconds) > 0)
        ? Number(route.durationSeconds)
        : (isArrived ? 0 : Math.round(distanceMeters / 7));

    const road = route ? window.payrollGetNextRoadName(route) : 'Road route';

    window.payrollRenderJourneyOverlay(mapData.journeyOverlay, {
        name: mapData.employeeName || 'You',
        distanceMeters: distanceMeters,
        durationSeconds: durationSeconds,
        speedMps: mapData.speedMps,
        accuracyMeters: mapData.lastAccuracyMeters,
        journeyStartedAt: mapData.journeyStartedAt,
        road: road,
        currentAddress: mapData.currentAddress || '',
        userCoords: user,
        withinRange: isWithin,
        arrived: isArrived
    });
};

window.payrollCreateJourneyOverlay = function (mapElement, className) {
    if (!mapElement) return null;
    if (!document.getElementById('payroll-journey-map-global-style')) {
        const style = document.createElement('style');
        style.id = 'payroll-journey-map-global-style';
        style.textContent = `
.payroll-admin-journey-tooltip{background:transparent!important;border:0!important;box-shadow:none!important;padding:0!important;color:inherit!important}.payroll-admin-journey-tooltip:before{display:none!important}.payroll-admin-journey-label{min-width:178px;max-width:235px;padding:7px 8px;border-radius:13px;background:rgba(255,255,255,.96);border:1px solid rgba(22,136,255,.20);box-shadow:0 9px 24px rgba(15,31,55,.24),0 2px 8px rgba(15,31,55,.12);backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);color:#172238;font-size:9px;line-height:1.15}.payroll-admin-journey-head{display:flex;align-items:center;justify-content:space-between;gap:7px}.payroll-admin-journey-name{font-size:11px;font-weight:900;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.payroll-admin-journey-state{font-size:7px;font-weight:900;white-space:nowrap}.payroll-admin-journey-destination{margin-top:3px;color:#718096;font-size:7px;font-weight:800}.payroll-admin-journey-grid{display:grid;grid-template-columns:1fr 1fr;gap:3px;margin-top:5px}.payroll-admin-journey-grid span{display:block;padding:4px 4px;border-radius:7px;background:#f1f5fa;border:1px solid rgba(19,43,77,.07);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.payroll-admin-journey-grid b{font-weight:900}.payroll-admin-journey-tooltip .leaflet-tooltip-content{margin:0!important}[data-theme="dark"] .payroll-admin-journey-label,[data-bs-theme="dark"] .payroll-admin-journey-label{background:rgba(14,22,35,.96);border-color:rgba(79,166,255,.25);box-shadow:0 12px 28px rgba(0,0,0,.48);color:#edf5ff}.payroll-admin-journey-grid span,[data-theme="dark"] .payroll-admin-journey-grid span,[data-bs-theme="dark"] .payroll-admin-journey-grid span{color:#25354a}.payroll-admin-journey-grid span{color:#25354a}[data-theme="dark"] .payroll-admin-journey-grid span,[data-bs-theme="dark"] .payroll-admin-journey-grid span{background:rgba(29,43,61,.78);border-color:rgba(143,177,214,.12);color:#dbeaff}.payroll-admin-journey-destination{color:#718096}[data-theme="dark"] .payroll-admin-journey-destination,[data-bs-theme="dark"] .payroll-admin-journey-destination{color:#8fa4bb}@media(max-width:900px){.payroll-admin-journey-label{min-width:150px;max-width:190px;padding:6px 7px}.payroll-admin-journey-name{font-size:10px}.payroll-admin-journey-grid{gap:2px}.payroll-admin-journey-grid span{padding:3px;font-size:8px}}.admin-employee-label,.payroll-employee-name-label{background:rgba(10,18,30,.92)!important;color:#fff!important;border:1px solid rgba(255,255,255,.18)!important;border-radius:10px!important;box-shadow:0 5px 14px rgba(0,0,0,.25)!important;font-size:11px!important;font-weight:800!important;padding:4px 8px!important}.admin-distance-label{background:rgba(13,110,253,.94)!important;color:#fff!important;border:0!important;border-radius:99px!important;font-weight:800!important;}
.payroll-journey-overlay{position:absolute!important;left:10px!important;right:10px!important;top:10px!important;bottom:auto!important;z-index:1000!important;width:auto!important;margin:0!important;padding:8px 12px!important;border-radius:12px!important;overflow:hidden;pointer-events:auto;color:#1e293b;background:rgba(255,255,255,.95);border:1px solid rgba(226,232,240,.9);box-shadow:0 8px 24px rgba(15,23,42,.12),0 2px 6px rgba(15,23,42,.06);backdrop-filter:blur(14px) saturate(140%);-webkit-backdrop-filter:blur(14px) saturate(140%);font-size:11px;line-height:1.25}
.payroll-journey-card{display:flex;align-items:center;justify-content:space-between;gap:12px;width:100%;overflow:hidden;background:transparent!important;padding:0!important}
.payroll-journey-info-group{display:flex;align-items:center;gap:10px;flex:1 1 auto;min-width:0}
.payroll-journey-avatar{width:32px;height:32px;display:grid;place-items:center;flex:0 0 32px;border-radius:9px;background:linear-gradient(135deg,#2563eb,#4f46e5);color:#fff;font-size:15px;box-shadow:0 3px 8px rgba(37,99,235,.3)}
.payroll-journey-arrived .payroll-journey-avatar{background:linear-gradient(135deg,#10b981,#059669);box-shadow:0 3px 8px rgba(16,185,129,.35)}
.payroll-journey-details{display:flex;flex-direction:column;gap:3px;min-width:0;flex:1 1 auto}
.payroll-journey-head{display:flex;align-items:center;gap:7px;min-width:0}
.payroll-journey-name{font-size:12.5px;font-weight:800;letter-spacing:-.1px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:#0f172a}
.payroll-journey-status{display:inline-flex;align-items:center;gap:4px;padding:2px 7px;border-radius:999px;font-size:8px;font-weight:800;letter-spacing:.3px;white-space:nowrap;flex:0 0 auto}
.payroll-journey-status.live{background:#e0f2fe;color:#0369a1;border:1px solid rgba(3,105,161,.22)}
.payroll-journey-status.outside{background:#fee2e2;color:#b91c1c;border:1px solid rgba(185,28,28,.22)}
.payroll-journey-status.arrived{background:#dcfce7;color:#15803d;border:1px solid rgba(21,128,61,.22)}
.payroll-journey-status .dot{width:5.5px;height:5.5px;border-radius:50%;background:currentColor}
.payroll-journey-status.live .dot{animation:payrollJourneyPulse 1.5s ease-in-out infinite}@keyframes payrollJourneyPulse{0%,100%{opacity:.55;transform:scale(.85)}50%{opacity:1;transform:scale(1.1)}}
.payroll-journey-sub{display:flex;align-items:center;gap:6px;min-width:0}
.payroll-journey-sub-item{display:inline-flex;align-items:center;gap:4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:240px;padding:2px 7px;border-radius:6px;font-size:9px;font-weight:600;line-height:1.2}
.payroll-journey-sub-item .chip-text{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.payroll-journey-sub-item.location{color:#065f46;background:rgba(16,185,129,.09);border:1px solid rgba(16,185,129,.2);font-weight:700}
.payroll-journey-sub-item.road{color:#1e40af;background:rgba(37,99,235,.08);border:1px solid rgba(37,99,235,.18)}
.payroll-journey-metrics{display:flex;align-items:center;gap:5px;flex:0 0 auto;overflow-x:auto;scrollbar-width:none;-webkit-overflow-scrolling:touch}
.payroll-journey-metrics::-webkit-scrollbar{display:none}
.payroll-journey-metric{flex:0 0 auto;min-width:64px;padding:3px 6px;border-radius:8px;background:rgba(248,250,252,.96);border:1px solid rgba(226,232,240,.9);text-align:center}
.payroll-journey-icon{font-size:8.5px;line-height:1;margin-bottom:1.5px}
.payroll-journey-label{font-size:6.5px;text-transform:uppercase;letter-spacing:.3px;font-weight:800;color:#64748b;line-height:1}
.payroll-journey-value{margin-top:1.5px;font-size:9px;line-height:1;font-weight:900;color:#0f172a;white-space:nowrap}
[data-theme="dark"] .payroll-journey-overlay,[data-bs-theme="dark"] .payroll-journey-overlay{color:#f1f5f9;background:rgba(15,23,42,.94);border-color:rgba(51,65,85,.8);box-shadow:0 10px 28px rgba(0,0,0,.45)}
[data-theme="dark"] .payroll-journey-card,[data-bs-theme="dark"] .payroll-journey-card{background:transparent!important}
[data-theme="dark"] .payroll-journey-name,[data-bs-theme="dark"] .payroll-journey-name,[data-theme="dark"] .payroll-journey-value,[data-bs-theme="dark"] .payroll-journey-value{color:#f8fafc}
[data-theme="dark"] .payroll-journey-label,[data-bs-theme="dark"] .payroll-journey-label{color:#94a3b8}
[data-theme="dark"] .payroll-journey-metric,[data-bs-theme="dark"] .payroll-journey-metric{background:rgba(30,41,59,.85);border-color:rgba(51,65,85,.8)}
[data-theme="dark"] .payroll-journey-sub-item.location,[data-bs-theme="dark"] .payroll-journey-sub-item.location{color:#6ee7b7;background:rgba(16,185,129,.16);border-color:rgba(16,185,129,.28)}
[data-theme="dark"] .payroll-journey-sub-item.road,[data-bs-theme="dark"] .payroll-journey-sub-item.road{color:#93c5fd;background:rgba(37,99,235,.16);border-color:rgba(37,99,235,.28)}
[data-theme="dark"] .payroll-journey-status.live,[data-bs-theme="dark"] .payroll-journey-status.live{background:rgba(3,105,161,.28);color:#7dd3fc;border-color:rgba(3,105,161,.4)}
[data-theme="dark"] .payroll-journey-status.outside,[data-bs-theme="dark"] .payroll-journey-status.outside{background:rgba(220,38,38,.28);color:#fca5a5;border-color:rgba(220,38,38,.4)}
[data-theme="dark"] .payroll-journey-status.arrived,[data-bs-theme="dark"] .payroll-journey-status.arrived{background:rgba(22,163,74,.28);color:#86efac;border-color:rgba(22,163,74,.4)}
@media(max-width:860px){.payroll-journey-card{flex-direction:column;align-items:stretch;gap:7px}.payroll-journey-metrics{width:100%;justify-content:flex-start}.payroll-journey-sub-item{max-width:170px}}
`;
        document.head.appendChild(style);
    }
    let overlay = mapElement.querySelector(`.${className}`);
    if (overlay) return overlay;
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
    const road = escapeHtml(data.road || 'Road route');
    const location = escapeHtml(data.currentAddress || (data.userCoords ? `${Number(data.userCoords[0]).toFixed(4)}, ${Number(data.userCoords[1]).toFixed(4)}` : 'Resolving location...'));
    const distance = window.payrollFormatRouteDistance(data.distanceMeters);
    const eta = data.durationSeconds > 0
        ? window.payrollFormatRouteDuration(data.durationSeconds)
        : (data.arrived ? 'Arrived' : (Number(data.distanceMeters) > 35 ? window.payrollFormatRouteDuration(Math.round(Number(data.distanceMeters) / 7)) : '1 min'));
    const speed = window.payrollFormatSpeed(data.speedMps);
    const accuracy = Number(data.accuracyMeters) > 0 ? `±${Math.round(Number(data.accuracyMeters))} m` : 'Unknown';
    const elapsed = data.journeyStartedAt ? window.payrollFormatRouteDuration((Date.now() - data.journeyStartedAt) / 1000) : '0s';
    const arrived = !!data.arrived;
    const withinRange = data.withinRange !== false;
    const statusText = arrived ? 'ARRIVED' : (withinRange ? 'IN RANGE' : 'OUTSIDE RANGE');
    const statusClass = arrived ? ' arrived' : (withinRange ? ' live' : ' outside');
    const cardClass = arrived ? ' payroll-journey-arrived' : '';
    const avatarIcon = arrived ? '🏁' : '🛵';

    overlay.innerHTML =
        `<div class="payroll-journey-card${cardClass}">` +
        `<div class="payroll-journey-info-group">` +
        `<div class="payroll-journey-avatar">${avatarIcon}</div>` +
        `<div class="payroll-journey-details">` +
        `<div class="payroll-journey-head">` +
        `<span class="payroll-journey-name">${name}</span>` +
        `<span class="payroll-journey-status${statusClass}"><span class="dot"></span>${statusText}</span>` +
        `</div>` +
        `<div class="payroll-journey-sub">` +
        `<span class="payroll-journey-sub-item location" title="Current Location: ${location}"><span class="chip-text">📍 ${location}</span></span>` +
        `<span class="payroll-journey-sub-item road" title="Road: ${road}"><span class="chip-text">🛣️ ${road}</span></span>` +
        `</div>` +
        `</div>` +
        `</div>` +
        `<div class="payroll-journey-metrics">` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">📏</div><div class="payroll-journey-label">Remaining</div><div class="payroll-journey-value">${distance}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">⏱️</div><div class="payroll-journey-label">ETA</div><div class="payroll-journey-value">${eta}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">🚦</div><div class="payroll-journey-label">Speed</div><div class="payroll-journey-value">${escapeHtml(speed)}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">🎯</div><div class="payroll-journey-label">Accuracy</div><div class="payroll-journey-value">${accuracy}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">🕐</div><div class="payroll-journey-label">Journey</div><div class="payroll-journey-value">${elapsed}</div></div>` +
        `<div class="payroll-journey-metric"><div class="payroll-journey-icon">🏢</div><div class="payroll-journey-label">Dest</div><div class="payroll-journey-value">Office</div></div>` +
        `</div>` +
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
            // ROUTE LINE (Hidden by default)
            // ------------------------------------------------

            const routeLine = L.polyline([], { color: "#0d6efd", weight: 3, opacity: 0, dashArray: "7,7" }).addTo(map);

            const roadRouteCasing = L.polyline([], { color: '#ffffff', weight: 8, opacity: 0, lineCap: 'round', lineJoin: 'round' }).addTo(map);
            const roadRouteLine = L.polyline([], { color: '#1688ff', weight: 5, opacity: 0, lineCap: 'round', lineJoin: 'round' }).addTo(map);
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
        mapData.userPosition = user.slice();

        // Update reverse geocoding for current address & locality
        window.payrollUpdateEmployeeAddress(mapData, user);

        // Immediate overlay render
        window.payrollRefreshEmployeeJourneyOverlay(mapData);

        // Presentation road routing: update UI when route loads, but don't block map readiness.
        window.payrollRequestJourneyRoute(mapData, user, office, { minMoveMeters: 20, minIntervalMs: 18000 }).then(function (employeeRoute) {
            if (employeeRoute?.geometry?.length > 1) {
                mapData.roadRouteCasing.setLatLngs(employeeRoute.geometry);
                mapData.roadRouteLine.setLatLngs(employeeRoute.geometry);

                if (mapData.showRouteToOffice) {
                    mapData.roadRouteCasing.setStyle({ opacity: .78 });
                    mapData.roadRouteLine.setStyle({ opacity: .98 });
                    mapData.routeLine.setStyle({ opacity: 0 });
                } else {
                    mapData.roadRouteCasing.setStyle({ opacity: 0 });
                    mapData.roadRouteLine.setStyle({ opacity: 0 });
                    mapData.routeLine.setStyle({ opacity: 0 });
                }
            } else {
                if (mapData.showRouteToOffice) {
                    mapData.routeLine.setLatLngs([]); mapData.routeLine.setStyle({ opacity: 0 });
                } else {
                    mapData.routeLine.setStyle({ opacity: 0 });
                }
            }
            window.payrollRefreshEmployeeJourneyOverlay(mapData, employeeRoute);
        }).catch(function () { });

        const employeeAnimationKey =
            'employee:' + mapId;

        if (!mapData.hasInitialView) {
            mapData.userMarker.setLatLng(user);
            mapData.routeLine.setLatLngs([]);
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

        if (typeof window.enhanceEmployeeGeoMap === 'function') {
            window.enhanceEmployeeGeoMap(mapId);
        }

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
        mapData.userPosition = target.slice();

        // Update reverse geocoding for current address & locality if moved
        window.payrollUpdateEmployeeAddress(mapData, target);

        window.payrollRequestJourneyRoute(mapData, target, office, { minMoveMeters: 20, minIntervalMs: 18000 }).then(function (route) {
            if (route?.geometry?.length > 1) {
                mapData.roadRouteCasing?.setLatLngs(route.geometry);
                mapData.roadRouteLine?.setLatLngs(route.geometry);

                if (mapData.showRouteToOffice) {
                    mapData.roadRouteCasing?.setStyle({ opacity: .78 });
                    mapData.roadRouteLine?.setStyle({ opacity: .98 });
                    mapData.routeLine?.setStyle({ opacity: 0 });
                } else {
                    mapData.roadRouteCasing?.setStyle({ opacity: 0 });
                    mapData.roadRouteLine?.setStyle({ opacity: 0 });
                    mapData.routeLine?.setStyle({ opacity: 0 });
                }
            } else if (mapData.showRouteToOffice) {
                mapData.routeLine?.setLatLngs([]); mapData.routeLine?.setStyle({ opacity: 0 });
            } else {
                mapData.routeLine?.setStyle({ opacity: 0 });
            }
            window.payrollRefreshEmployeeJourneyOverlay(mapData, route);
        }).catch(function () { });

        window.payrollSmoothMoveMarker(
            mapData.userMarker,
            'employee:' + mapId,
            target,
            duration,
            function (position) {
                try {
                    mapData.routeLine.setLatLngs([]);
                    mapData.userPosition = position;
                    window.payrollRefreshEmployeeJourneyOverlay(mapData);
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


        try { if (mapData?._layoutObserver) mapData._layoutObserver.disconnect(); } catch (_) { }

        delete window.payrollGeoMaps[mapId];
    };



// ============================================================
// PREMIUM MAP UI PRESENTATION
// ============================================================
// The controls are created dynamically by the map runtime. Keep their styling
// here so they remain above Leaflet panes, readable in light/dark themes,
// and usable on narrow screens without changing the existing map flow.
window.ensurePayrollPremiumMapStyles = function () {
    if (document.getElementById('payroll-premium-map-styles')) return;

    const style = document.createElement('style');
    style.id = 'payroll-premium-map-styles';
    style.textContent = `
      .payroll-premium-map-ui {
        position:absolute;
        z-index:1000;
        left:14px;
        right:14px;
        top:14px;
        pointer-events:none;
        font-family:inherit;
      }
      .payroll-premium-employee-map-ui {
        position:absolute !important;
        z-index:1000 !important;
        left:10px !important;
        right:10px !important;
        bottom:10px !important;
        top:auto !important;
        width:auto !important;
        display:flex !important;
        flex-direction:row !important;
        align-items:center !important;
        justify-content:space-between !important;
        gap:8px !important;
        pointer-events:none !important;
        font-family:inherit !important;
      }

      .payroll-employee-map-tools {
        display:flex !important;
        align-items:center !important;
        flex-direction:row !important;
        justify-content:flex-start !important;
        flex-wrap:nowrap !important;
        gap:5px !important;
        width:auto !important;
        max-width:calc(100% - 95px) !important;
        padding:4px 6px !important;
        border:1px solid rgba(255,255,255,.2) !important;
        border-radius:12px !important;
        background:rgba(255,255,255,.94) !important;
        box-shadow:0 8px 24px rgba(15,23,42,.14) !important;
        backdrop-filter:blur(14px) !important;
        -webkit-backdrop-filter:blur(14px) !important;
        pointer-events:auto !important;
        overflow-x:auto !important;
        scrollbar-width:none !important;
      }
      .payroll-employee-map-tools::-webkit-scrollbar { display:none !important; }

      .payroll-map-commandbar {
        display:flex;
        align-items:stretch;
        flex-direction:column;
        flex-wrap:nowrap;
        gap:7px;
        width:44px;
        max-width:44px;
        padding:6px;
        border:1px solid rgba(255,255,255,.18);
        border-radius:14px;
        background:rgba(255,255,255,.94);
        box-shadow:0 10px 28px rgba(15,23,42,.14);
        backdrop-filter:blur(14px);
        -webkit-backdrop-filter:blur(14px);
        pointer-events:auto;
        transition:width .2s ease, max-width .2s ease, padding .2s ease;
      }

      .payroll-map-commandbar {
        position:absolute;
        top:50%;
        left:0;
        transform:translateY(-50%);
      }

      .payroll-map-commandbar .payroll-map-search-wrap,
      .payroll-map-commandbar .payroll-map-filter {
        display:none;
      }

      .payroll-map-commandbar.expanded {
        width:226px;
        max-width:226px;
      }

      .payroll-map-commandbar.expanded .payroll-map-search-wrap,
      .payroll-map-commandbar.expanded .payroll-map-filter {
        display:flex;
      }

      .payroll-map-commandbar.expanded .payroll-map-tools-group {
        display:flex;
        flex-direction:column;
        align-items:stretch;
      }

      .payroll-map-commandbar.is-fullscreen-hidden {
        display:none !important;
      }

      .payroll-map-tools-group {
        display:none;
        gap:7px;
        align-items:stretch;
      }

      .payroll-map-search-wrap {
        display:flex;
        align-items:center;
        min-width:160px;
        height:36px;
        padding:0 10px;
        border-radius:10px;
        background:rgba(255,255,255,.10);
        border:1px solid rgba(255,255,255,.16);
        transition: width .3s ease;
      }

      .payroll-map-search-icon {
        margin-right:7px;
        color:#64748b;
        font-size:16px;
      }

      .payroll-map-search {
        width:100%;
        min-width:0;
        border:0;
        outline:0;
        color:#172238;
        background:transparent;
        font-size:12px;
      }

      .payroll-map-search::placeholder { color:#94a3b8; }

      .payroll-map-filter {
        height:36px;
        border:1px solid rgba(255,255,255,.16);
        border-radius:10px;
        padding:0 10px;
        color:#172238;
        background:#f8fafc;
        font-size:12px;
        pointer-events:auto;
      }

      .payroll-map-tool,
      .payroll-map-tool-toggle {
        height:36px;
        min-width:72px;
        width:100%;
        padding:0 11px;
        border:1px solid rgba(148,163,184,.28);
        border-radius:10px;
        color:#334155;
        background:rgba(248,250,252,.96);
        font:600 11px/1 inherit;
        cursor:pointer;
        transition:transform .15s ease,background .15s ease,border-color .15s ease;
      }

      .payroll-employee-map-tools button {
        height:28px !important;
        min-width:0 !important;
        width:auto !important;
        padding:0 8px !important;
        border:1px solid rgba(148,163,184,.28) !important;
        border-radius:8px !important;
        color:#1e293b !important;
        background:rgba(248,250,252,.96) !important;
        font:700 11px/1 inherit !important;
        cursor:pointer !important;
        white-space:nowrap !important;
        display:inline-flex !important;
        align-items:center !important;
        justify-content:center !important;
        gap:4px !important;
        flex:0 0 auto !important;
        transition:transform .15s ease,background .15s ease,border-color .15s ease;
      }

      .payroll-map-tool:hover,
      .payroll-map-tool-toggle:hover,
      .payroll-employee-map-tools button:hover {
        background:rgba(226,232,240,.98);
        border-color:rgba(96,165,250,.7);
        transform:translateY(-1px);
      }

      .payroll-map-tool.active,
      .payroll-map-tool-toggle.active,
      .payroll-employee-map-tools button.active {
        background:rgba(37,99,235,.92);
        border-color:rgba(147,197,253,.85);
        color:white;
      }

      .payroll-map-statusbar {
        display:flex;
        align-items:center;
        gap:14px;
        width:fit-content;
        max-width:100%;
        margin-top:7px;
        padding:6px 10px;
        border-radius:10px;
        background:rgba(15,23,42,.78);
        color:#64748b;
        font-size:10px;
        box-shadow:0 6px 18px rgba(0,0,0,.20);
        pointer-events:none;
      }

      .payroll-employee-map-live {
        display:inline-flex !important;
        align-items:center !important;
        gap:5px !important;
        padding:5px 9px !important;
        margin:0 !important;
        border-radius:9px !important;
        background:rgba(15,23,42,.88) !important;
        color:#fff !important;
        font-size:10px !important;
        font-weight:800 !important;
        pointer-events:auto !important;
        box-shadow:0 6px 18px rgba(0,0,0,.20) !important;
        flex:0 0 auto !important;
      }

      .payroll-map-statusbar i,
      .payroll-map-live-dot {
        display:inline-block;
        width:7px;
        height:7px;
        margin-right:4px;
        border-radius:50%;
        background:#22c55e;
        box-shadow:0 0 9px rgba(34,197,94,.7);
      }

      .payroll-map-status-stale i { background:#f59e0b; }
      .payroll-map-status-out i { background:#ef4444; }

      .admin-map-wrapper:fullscreen,
      .admin-map-wrapper:-webkit-full-screen {
        width: 100vw !important;
        height: 100vh !important;
        border-radius: 0 !important;
        background: #08111f !important;
        position: relative !important;
      }

      .admin-map-wrapper:fullscreen .admin-live-map,
      .admin-map-wrapper:-webkit-full-screen .admin-live-map {
        width: 100vw !important;
        height: 100vh !important;
      }

      .admin-map-wrapper.payroll-map-fullscreen,
      .payroll-map-fullscreen {
        position: fixed !important;
        inset: 0 !important;
        width: 100vw !important;
        height: 100vh !important;
        z-index: 99999 !important;
        border-radius: 0 !important;
        background: #08111f !important;
      }

      .admin-map-wrapper.payroll-map-fullscreen .admin-live-map {
        width: 100vw !important;
        height: 100vh !important;
      }

      body.payroll-fullscreen-active {
        overflow: hidden !important;
      }

      .payroll-premium-employee-map-ui {
        position:absolute !important;
        z-index:1000 !important;
        left:10px !important;
        right:10px !important;
        bottom:10px !important;
        top:auto !important;
        transform:none !important;
        width:auto !important;
        display:flex !important;
        flex-direction:row !important;
        align-items:center !important;
        justify-content:space-between !important;
        gap:8px !important;
        pointer-events:none !important;
        font-family:inherit !important;
      }

      /* The map controls follow the application's light/dark theme. A manually
         selected map layer is still respected by the map runtime. */
      [data-theme="dark"] .payroll-map-commandbar,
      [data-bs-theme="dark"] .payroll-map-commandbar,
      body.dark .payroll-map-commandbar,
      [data-theme="dark"] .payroll-employee-map-tools,
      [data-bs-theme="dark"] .payroll-employee-map-tools,
      body.dark .payroll-employee-map-tools {
        background:rgba(15,23,42,.90);
        border-color:rgba(255,255,255,.18);
        box-shadow:0 10px 28px rgba(0,0,0,.28);
      }
      [data-theme="dark"] .payroll-map-search-wrap,
      [data-bs-theme="dark"] .payroll-map-search-wrap,
      body.dark .payroll-map-search-wrap {
        background:rgba(255,255,255,.10);
        border-color:rgba(255,255,255,.16);
      }
      [data-theme="dark"] .payroll-map-search,
      [data-bs-theme="dark"] .payroll-map-search,
      body.dark .payroll-map-search { color:#f8fafc; }
      [data-theme="dark"] .payroll-map-filter,
      [data-bs-theme="dark"] .payroll-map-filter,
      body.dark .payroll-map-filter {
        color:#f8fafc; background:#1e293b; border-color:rgba(255,255,255,.16);
      }
      [data-theme="dark"] .payroll-map-tool,
      [data-bs-theme="dark"] .payroll-map-tool,
      body.dark .payroll-map-tool,
      [data-theme="dark"] .payroll-map-tool-toggle,
      [data-bs-theme="dark"] .payroll-map-tool-toggle,
      body.dark .payroll-map-tool-toggle,
      [data-theme="dark"] .payroll-employee-map-tools button,
      [data-bs-theme="dark"] .payroll-employee-map-tools button,
      body.dark .payroll-employee-map-tools button {
        color:#e2e8f0; background:rgba(30,41,59,.88);
        border-color:rgba(148,163,184,.28);
      }
      [data-theme="dark"] .payroll-map-tool:hover,
      [data-bs-theme="dark"] .payroll-map-tool:hover,
      body.dark .payroll-map-tool:hover,
      [data-theme="dark"] .payroll-map-tool-toggle:hover,
      [data-bs-theme="dark"] .payroll-map-tool-toggle:hover,
      body.dark .payroll-map-tool-toggle:hover,
      [data-theme="dark"] .payroll-employee-map-tools button:hover,
      [data-bs-theme="dark"] .payroll-employee-map-tools button:hover,
      body.dark .payroll-employee-map-tools button:hover {
        background:rgba(51,65,85,.98);
      }
      [data-theme="dark"] .payroll-map-statusbar,
      [data-bs-theme="dark"] .payroll-map-statusbar,
      body.dark .payroll-map-statusbar,
      [data-theme="dark"] .payroll-employee-map-live,
      [data-bs-theme="dark"] .payroll-employee-map-live,
      body.dark .payroll-employee-map-live {
        background:rgba(15,23,42,.82); color:#cbd5e1;
      }

      @media (max-width: 900px) {
        .payroll-premium-map-ui {
          left:8px;
          right:8px;
          top:8px;
        }
        .payroll-map-commandbar {
          left:0;
          right:auto;
          top:50%;
          width:42px;
          max-width:42px;
          overflow:visible;
        }
        .payroll-map-commandbar.expanded { width:210px; max-width:210px; }
        .payroll-map-search-wrap { min-width:0; width:100%; }
        .payroll-map-filter { width:100%; }
        .payroll-map-tool { min-width:62px; width:100%; }
      }

      @media (max-width: 600px) {
        .payroll-map-statusbar {
          gap:8px;
          font-size:9px;
          overflow:hidden;
          white-space:nowrap;
        }
        .payroll-map-commandbar {
          left:0;
          top:50%;
          width:40px;
          max-width:40px;
          padding:5px;
          gap:5px;
        }
        .payroll-map-commandbar.expanded { width:196px; max-width:196px; }
        .payroll-map-filter { max-width:none; width:100%; }
        .payroll-map-tool span,
        .payroll-map-tool-toggle span { display:none; }
        .payroll-map-tool,
        .payroll-map-tool-toggle { min-width:38px; padding:0 8px; }

        .payroll-premium-employee-map-ui .payroll-employee-map-tools {
          left:0 !important;
          right:0 !important;
          bottom:0 !important;
          top:auto !important;
          width:100% !important;
          max-width:100% !important;
          display:flex !important;
          flex-direction:row !important;
          align-items:center !important;
          justify-content:space-between !important;
          flex-wrap:nowrap !important;
          gap:4px !important;
          padding:4px 6px !important;
          box-sizing:border-box !important;
          overflow-x:auto !important;
          scrollbar-width:none !important;
        }
        .payroll-premium-employee-map-ui .payroll-employee-map-tools button {
          flex:0 0 auto !important;
          min-width:0 !important;
          height:28px !important;
          padding:0 7px !important;
          box-sizing:border-box !important;
          font-size:10px !important;
        }
        .payroll-premium-employee-map-ui .payroll-employee-map-tools button span {
          display:inline !important;
          margin-left:2px;
        }
        .payroll-premium-employee-map-ui .payroll-employee-map-live {
          left:8px !important;
          right:8px !important;
          bottom:8px !important;
          width:max-content;
          max-width:calc(100% - 16px);
        }
      }

      @media (max-width: 320px) {
        .payroll-premium-employee-map-ui .payroll-employee-map-tools button {
          font-size:9px !important;
          height:30px !important;
        }
      }

      .leaflet-control-zoom {
        margin-top:74px !important;
      }

      .payroll-premium-employee-map-ui + * { pointer-events:auto; }
    `;
    document.head.appendChild(style);
};
window.ensurePayrollPremiumMapStyles();

// ============================================================
// API-KEY-FREE MAP TILE PRESENTATION
// ============================================================

window.ensurePayrollDarkOsmTiles = function () {
    if (document.getElementById('payroll-dark-osm-tile-style')) return;

    const style = document.createElement('style');
    style.id = 'payroll-dark-osm-tile-style';
    style.textContent =
        '.leaflet-tile-pane .payroll-dark-osm-tiles {' +
        'filter: invert(0.88) hue-rotate(180deg) brightness(0.72) contrast(1.05) saturate(0.72);' +
        '}';
    document.head.appendChild(style);
};

window.ensurePayrollDarkOsmTiles();

window.ensurePayrollStayPinStyles = function () {
    if (document.getElementById('payroll-stay-pin-style')) return;

    const style = document.createElement('style');
    style.id = 'payroll-stay-pin-style';
    style.textContent =
        '@keyframes payrollPulseStay {' +
        '0% { transform: scale(0.85); opacity: 0.85; }' +
        '50% { transform: scale(1.4); opacity: 0.25; }' +
        '100% { transform: scale(1.75); opacity: 0; }' +
        '}' +
        '.payroll-stay-hub-pin-icon { background: transparent !important; border: none !important; }' +
        '.payroll-stay-hub-marker { display: flex; flex-direction: column; align-items: center; cursor: pointer; user-select: none; transition: transform 0.2s cubic-bezier(0.34, 1.56, 0.64, 1); }' +
        '.payroll-stay-hub-marker:hover { transform: scale(1.1); }';
    document.head.appendChild(style);
};

window.ensurePayrollStayPinStyles();

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
    const useCollisionOffsets = true;

    (Array.isArray(liveStaff) ? liveStaff : [])
        .slice()
        .sort(function (a, b) { return Number(a.employeeId) - Number(b.employeeId); })
        .forEach(function (x) {
            const employeeId = Number(x.employeeId);
            const lat = Number(x.latitude);
            const lng = Number(x.longitude);
            if (!Number.isFinite(employeeId) || !Number.isFinite(lat) || !Number.isFinite(lng)) return;
            const speedMps = Number(x.speedMps ?? x.SpeedMps ?? 0);
            const isWithin = Boolean(x.isWithinAllowedRadius ?? x.IsWithinAllowedRadius);
            const rawName = String(x.name || ('Staff #' + employeeId)).trim();
            const item = {
                employeeId: employeeId,
                name: rawName,
                lat: lat,
                lng: lng,
                speedMps: speedMps,
                isWithinAllowedRadius: isWithin,
                offsetX: 0,
                offsetY: 0,
                centerLat: lat,
                centerLng: lng,
                isClustered: false
            };
            items.push(item);
            byId[employeeId] = item;
        });

    byId._clusters = [];
    if (!useCollisionOffsets || items.length < 2) return byId;

    // Union-find spatial clustering
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

            // Staff must be physically standing at the same spot (<= 5 meters)
            // to ever be considered in a shared stay/desk collision.
            // Never cluster staff who are in different houses, rooms, or streets (> 6m apart).
            if (d > 5.0) continue;

            // Check if their marker icons actually overlap on the current map zoom:
            let p1 = null, p2 = null;
            try {
                p1 = map.latLngToLayerPoint([items[i].lat, items[i].lng]);
                p2 = map.latLngToLayerPoint([items[j].lat, items[j].lng]);
            } catch (_) { }

            const pixelDist = (p1 && p2) ? p1.distanceTo(p2) : 999;
            // Only cluster if the markers visually collide on screen (< 24 pixels):
            if (pixelDist < 24) {
                union(i, j);
            }
        }
    }

    const groups = {};
    items.forEach(function (item, index) {
        const root = find(index);
        if (!groups[root]) groups[root] = [];
        groups[root].push(item);
    });

    const activeClusters = [];
    Object.keys(groups).forEach(function (root) {
        const group = groups[root];
        if (group.length < 2) return;

        // Stable employee-ID ordering prevents markers from swapping places.
        group.sort(function (a, b) { return a.employeeId - b.employeeId; });

        // Calculate centroid of the cluster
        const avgLat = group.reduce(function (sum, it) { return sum + it.lat; }, 0) / group.length;
        const avgLng = group.reduce(function (sum, it) { return sum + it.lng; }, 0) / group.length;
        const center = [avgLat, avgLng];
        const centerPoint = map.latLngToLayerPoint(center);
        const count = group.length;
        // Non-overlapping gentle fanning radius in pixels (just enough so both cards are clickable)
        const radius = count <= 2 ? 16 : count <= 4 ? 22 : count <= 7 ? 28 : 34;

        const clusterKey = 'stay_hub_' + group.map(function (it) { return it.employeeId; }).join('_');
        const staffNames = group.map(function (it) { return it.name; });

        activeClusters.push({
            key: clusterKey,
            centerLat: avgLat,
            centerLng: avgLng,
            count: count,
            staffNames: staffNames,
            employeeIds: group.map(function (it) { return it.employeeId; })
        });

        group.forEach(function (item, index) {
            let point;
            if (count === 2) {
                // Symmetrical horizontal side-by-side placement for 2 employees
                const sign = index === 0 ? -1 : 1;
                point = L.point(
                    centerPoint.x + sign * radius,
                    centerPoint.y
                );
            } else {
                const angle = (-Math.PI / 2) + (index * (Math.PI * 2 / count));
                point = L.point(
                    centerPoint.x + Math.cos(angle) * radius,
                    centerPoint.y + Math.sin(angle) * radius
                );
            }
            const display = map.layerPointToLatLng(point);
            item.offsetX = display.lng - item.lng;
            item.offsetY = display.lat - item.lat;
            item.centerLat = avgLat;
            item.centerLng = avgLng;
            item.isClustered = true;
            item.clusterKey = clusterKey;
        });
    });

    byId._clusters = activeClusters;
    return byId;
};

window.payrollUpdateAdminStayHubMarkers = function (state, activeClusters) {
    if (!state || !state.map) return;
    window.ensurePayrollStayPinStyles?.();
    state.stayHubMarkers = state.stayHubMarkers || {};
    const clusters = Array.isArray(activeClusters) ? activeClusters : [];
    const activeKeys = new Set(clusters.map(function (c) { return c.key; }));

    // 1. Remove stale stay hub markers that are no longer clustered
    Object.keys(state.stayHubMarkers).forEach(function (key) {
        if (!activeKeys.has(key)) {
            try {
                state.map.removeLayer(state.stayHubMarkers[key]);
            } catch (_) { }
            delete state.stayHubMarkers[key];
        }
    });

    // 2. Add or update active stay hub markers at the exact stayed coordinate
    clusters.forEach(function (c) {
        const pos = [c.centerLat, c.centerLng];
        const namesStr = c.staffNames.join(', ');
        const hubHtml =
            '<div class="payroll-stay-hub-marker" style="position:relative;display:flex;flex-direction:column;align-items:center;cursor:pointer;">' +
            '<div style="position:absolute;top:-4px;width:38px;height:38px;border-radius:50%;background:rgba(37,99,235,0.28);animation:payrollPulseStay 2s infinite ease-out;pointer-events:none;"></div>' +
            '<div style="width:30px;height:30px;border-radius:50%;background:linear-gradient(135deg,#1d4ed8,#2563eb);border:2px solid #ffffff;box-shadow:0 3px 10px rgba(0,0,0,0.38);display:flex;align-items:center;justify-content:center;color:#ffffff;font-size:14px;font-weight:700;z-index:2;">' +
            '📍' +
            '</div>' +
            '<div style="margin-top:2px;background:#0f172a;color:#ffffff;font-size:10px;font-weight:700;padding:2px 7px;border-radius:10px;box-shadow:0 2px 6px rgba(0,0,0,0.35);white-space:nowrap;letter-spacing:0.3px;border:1px solid rgba(255,255,255,0.25);z-index:2;">' +
            'Stay Point (' + c.count + ')' +
            '</div>' +
            '</div>';

        const hubIcon = L.divIcon({
            className: 'payroll-stay-hub-pin-icon',
            html: hubHtml,
            iconSize: [80, 52],
            iconAnchor: [40, 15]
        });

        const buildTooltipHtml = function (address) {
            const safeAddr = address ? (window.payrollEscapeHtml ? window.payrollEscapeHtml(address) : address) : '';
            return '<div class="admin-live-hover-card" style="min-width:185px;max-width:280px;">' +
                '<div class="admin-live-hover-title">' +
                '<span class="hover-avatar" style="background:#2563eb;color:#ffffff;">📍</span>' +
                '<strong>Shared Stay Location</strong>' +
                '<span class="hover-state within" style="background:rgba(37,99,235,0.15);color:#2563eb;border-color:rgba(37,99,235,0.3);font-size:10px;">' + c.count + ' Staff</span>' +
                '</div>' +
                '<div style="font-size:11px;color:#94a3b8;margin-top:4px;">' +
                '👥 <strong>' + (window.payrollEscapeHtml ? window.payrollEscapeHtml(namesStr) : namesStr) + '</strong>' +
                '</div>' +
                '<div style="font-size:11px;color:#cbd5e1;margin-top:5px;line-height:1.3;display:flex;align-items:flex-start;gap:4px;border-top:1px solid rgba(255,255,255,0.12);padding-top:4px;">' +
                '<span>📍</span> <span>' + (safeAddr || 'Resolving stay address...') + '</span>' +
                '</div>' +
                '</div>';
        };

        if (!state.stayHubMarkers[c.key]) {
            const marker = L.marker(pos, {
                icon: hubIcon,
                zIndexOffset: 1500,
                riseOnHover: true
            }).addTo(state.map);

            marker.bindTooltip(buildTooltipHtml(''), {
                direction: 'top',
                offset: [0, -18],
                opacity: 0.98,
                className: 'payroll-admin-hover-tooltip'
            });

            marker.on('click', function (e) {
                if (e?.originalEvent) e.originalEvent._stoppedByMarker = true;
                marker.openTooltip();
            });

            if (typeof window.payrollReverseGeocodeLocation === 'function') {
                window.payrollReverseGeocodeLocation(c.centerLat, c.centerLng).then(function (addr) {
                    if (addr && marker._map) {
                        marker._stayAddress = addr;
                        marker.setTooltipContent(buildTooltipHtml(addr));
                    }
                }).catch(function () { });
            }

            state.stayHubMarkers[c.key] = marker;
        } else {
            const existingMarker = state.stayHubMarkers[c.key];
            existingMarker.setLatLng(pos);
            existingMarker.setIcon(hubIcon);
            if (existingMarker._stayAddress) {
                existingMarker.setTooltipContent(buildTooltipHtml(existingMarker._stayAddress));
            }
        }
    });
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
                                maxZoom: 15,
                                animate: false
                            });
                        } else if (points.length === 1) {
                            map.setView(points[0], 15, { animate: false });
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

window.refreshAdminLiveMapSummary = function (mapId) {
    try {
        const state = window.adminLiveMaps?.[mapId];
        if (!state?.map) return;

        const now = Date.now();
        let live = 0;

        const staff = Array.isArray(state.liveStaff) ? state.liveStaff : [];
        staff.forEach(function (x) {
            const status = String(x.status || '').toLowerCase();
            if (status === 'live') live++;
        });

        if (live === 0 && state.liveData) {
            Object.keys(state.liveData || {}).forEach(function (key) {
                const id = Number(key);
                const last = Number(state.realtimeLastTimestamp?.[id]) ||
                    Number(state.realtimeLastAt?.[id]) || 0;
                const age = last > 0 ? Math.max(0, now - last) : Infinity;
                if (age <= 300000) live++;
            });
        }

        const labels = document.querySelectorAll(
            `[data-live-count-for="${mapId}"]`
        );
        labels.forEach(function (label) {
            const current = String(label.textContent || '');
            const match = current.match(/\/\s*(\d+)/);
            const total = match ? match[1] : '';
            const dot = label.querySelector('.live-count-dot');
            label.innerHTML = '';
            if (dot) label.appendChild(dot);
            label.appendChild(document.createTextNode(` ${live} Live${total ? ' / ' + total : ''}`));
        });

        const totalStaff = staff.length || Object.keys(state.liveData || {}).length;
        const overlays = document.querySelectorAll(
            `[data-live-empty-overlay="${mapId}"]`
        );
        overlays.forEach(function (overlay) {
            overlay.style.display = totalStaff === 0 ? '' : 'none';
        });
    } catch (error) {
        // Summary updates are presentation-only and must never affect live GPS.
        console.debug('Admin live summary update skipped:', error);
    }
};

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
                !Number.isFinite(longitude)
            ) {
                return;
            }

            const state =
                window.adminLiveMaps?.[mapId];

            if (!state?.map || !state.markers?.[employeeId]) {
                // The Blazor lifecycle path will add a marker when a new
                // employee/session first appears. Do not manufacture a marker
                // from a coordinate-only browser event.
                return;
            }

            const marker =
                state.markers[employeeId];

            // A live marker is valid only when Firebase explicitly says the
            // current record is ACTIVE and carries a real SessionId.
            const liveState = String(
                data.State ?? data.state ?? ''
            ).trim().toUpperCase();

            const incomingSession = String(
                data.SessionId ?? data.sessionId ?? ''
            ).trim();

            if (liveState === 'ENDED' || liveState === 'OFFLINE') {
                try {
                    if (state.map.hasLayer(marker)) {
                        state.map.removeLayer(marker);
                    }
                } catch { }

                try {
                    if (state.lines?.[employeeId]) state.map.removeLayer(state.lines[employeeId]);
                } catch { }
                try {
                    if (state.trails?.[employeeId]) state.map.removeLayer(state.trails[employeeId]);
                } catch { }

                delete state.markers[employeeId];
                delete state.markerSessions?.[employeeId];
                delete state.lastRealtimeAt?.[employeeId];
                delete state.realtimeLastTimestamp?.[employeeId];
                if (state.liveData) delete state.liveData[employeeId];
                window.refreshAdminLiveMapSummary(mapId);
                return;
            }

            // An incomplete browser/Firebase event is not an authoritative
            // removal signal. Preserve the last known-good live marker.
            // Allow ACTIVE, ONLINE, or empty state if marker already exists on the map.
            if (liveState !== '' && liveState !== 'ACTIVE' && liveState !== 'ONLINE') {
                return;
            }

            if (!incomingSession && !state.markerSessions?.[employeeId]) {
                return;
            }

            const currentSession = String(
                state.markerSessions?.[employeeId] || ''
            ).trim();

            /*
             * A different ACTIVE SessionId is a legitimate lifecycle boundary.
             * Adopt the new session immediately. The previous session remains
             * historical; this browser fast path never rewrites it.
             */
            const sessionChanged =
                !!incomingSession && !!currentSession && currentSession !== incomingSession;

            if (sessionChanged) {
                state.markerSessions[employeeId] = incomingSession;
                state.realtimeLastAt = state.realtimeLastAt || {};
                delete state.realtimeLastAt[employeeId];
            } else if (incomingSession) {
                state.markerSessions = state.markerSessions || {};
                state.markerSessions[employeeId] = incomingSession;
            }

            const incomingTimestampRaw =
                data.LastUpdatedUtc ??
                data.lastUpdatedUtc ??
                data.Timestamp ??
                data.timestamp ??
                '';

            const incomingTimestamp =
                Date.parse(String(incomingTimestampRaw));

            const lastTimestamp =
                Number(state.realtimeLastTimestamp?.[employeeId]) || 0;

            /*
             * Ignore an older GPS packet from a previous network queue. A new
             * session is allowed to reset this timestamp fence.
             */
            if (!sessionChanged &&
                Number.isFinite(incomingTimestamp) &&
                lastTimestamp > 0 &&
                incomingTimestamp < lastTimestamp) {
                return;
            }

            state.realtimeLastTimestamp =
                state.realtimeLastTimestamp || {};

            if (Number.isFinite(incomingTimestamp)) {
                state.realtimeLastTimestamp[employeeId] =
                    incomingTimestamp;
            }

            const target = [
                latitude,
                longitude
            ];

            const office = state.office;
            const radius = Number(state.lastOfficeRadius) || 0;
            let realtimeWithin = Boolean(data.IsWithinAllowedRadius ?? data.isWithinAllowedRadius);
            if (Array.isArray(office) && office.length === 2 && radius > 0 &&
                typeof window.payrollHaversineMeters === 'function') {
                const liveDistance = window.payrollHaversineMeters(target, office);
                realtimeWithin = liveDistance <= radius + 1;
            }

            // Keep the complete set of currently known live coordinates so a
            // single realtime GPS update cannot accidentally drop collision
            // offsets for neighbouring employees. These remain exact GPS points.
            state.liveData = state.liveData || {};
            state.liveData[employeeId] = {
                employeeId: employeeId,
                latitude: latitude,
                longitude: longitude,
                speedMps: Number(data.SpeedMps ?? data.speedMps ?? marker._speedMps) || 0,
                isWithinAllowedRadius: realtimeWithin
            };

            const metaName = String(marker._adminEmployeeName || 'Employee').trim();
            const metaParts = metaName.split(/\s+/).filter(Boolean);
            const metaInitials = metaParts.length === 1
                ? metaParts[0].slice(0, 1)
                : (metaParts[0][0] + metaParts[metaParts.length - 1][0]);
            
            const metaSpeedMps = Number(data.SpeedMps ?? data.speedMps ?? marker._speedMps) || 0;
            const metaSpeedKmh = metaSpeedMps * 3.6;
            const useSpeedIcons = !!(window.payrollCompanySettings && window.payrollCompanySettings.useSpeedBasedMarkers);
            const speedTier = useSpeedIcons ? window.payrollGetSpeedEmoji(metaSpeedKmh) : '';
            const visualKey =
                (realtimeWithin ? 'within' : 'outside') +
                '|active|' + speedTier;

            if (marker._adminVisualKey !== visualKey) {
                const realtimeHtml = window.payrollMakeAdminMarkerHtml(
                    metaInitials,
                    realtimeWithin ? 'within' : 'outside',
                    '',
                    metaSpeedKmh,
                    metaName,
                    useSpeedIcons
                );
                const realtimeIcon = L.divIcon({
                    className: 'payroll-user-marker',
                    html: realtimeHtml,
                    iconSize: [46, 54],
                    iconAnchor: [23, 54]
                });
                marker.setIcon(realtimeIcon);
                marker._adminVisualKey = visualKey;
            }

            // Update tooltip content in realtime with live address
            try {
                const safeRole = window.escapeAdminHtml ? window.escapeAdminHtml(String(marker._role || 'Staff')) : 'Staff';
                const safeDisplayName = window.escapeAdminHtml ? window.escapeAdminHtml(metaName) : metaName;
                const speedLabel = window.payrollGetSpeedLabel(metaSpeedKmh);
                const speedEmoji = window.payrollGetSpeedEmoji(metaSpeedKmh);
                const rangeClass = realtimeWithin ? 'within' : 'outside';
                const rangeLabel = realtimeWithin ? '✅ In range' : '🔴 Outside';

                const hoverHtml = window.payrollMakeAdminMarkerTooltipHtml(
                    metaInitials,
                    safeDisplayName,
                    rangeClass,
                    rangeLabel,
                    safeRole,
                    speedEmoji,
                    speedLabel,
                    marker._currentAddress
                );

                if (marker.getTooltip()) {
                    marker.setTooltipContent(hoverHtml);
                }

                // Asynchronously resolve and update location address/name
                if (typeof window.payrollFetchMarkerAddress === 'function') {
                    window.payrollFetchMarkerAddress(marker, latitude, longitude, function (addr) {
                        try {
                            if (marker.getTooltip()) {
                                marker.setTooltipContent(window.payrollMakeAdminMarkerTooltipHtml(
                                    metaInitials,
                                    safeDisplayName,
                                    rangeClass,
                                    rangeLabel,
                                    safeRole,
                                    speedEmoji,
                                    speedLabel,
                                    addr
                                ));
                            }
                        } catch { }
                    });
                }

                marker._speedMps = metaSpeedMps;
            } catch { }

            marker._adminWithinRange = realtimeWithin;

            const collisionStaff = Object.keys(state.liveData || {}).map(function (key) {
                return state.liveData[key];
            });

            const displayItems =
                typeof window.payrollBuildAdminMarkerDisplayPositions === 'function'
                    ? window.payrollBuildAdminMarkerDisplayPositions(
                        state.map,
                        collisionStaff,
                        state.lastSelectedId || 0)
                    : {};

            if (typeof window.payrollUpdateAdminStayHubMarkers === 'function') {
                window.payrollUpdateAdminStayHubMarkers(state, displayItems._clusters);
            }

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
            // Max is 62 000 ms to cover full 60-second GPS update intervals —
            // the marker will glide continuously from fix to fix with no jumps.
            const duration =
                Math.max(
                    1200,
                    Math.min(
                        62000,
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
                            if (displayItem?.isClustered) {
                                const connectorOrigin = [displayItem.centerLat, displayItem.centerLng];
                                if (!state.collisionConnectors?.[employeeId]) {
                                    state.collisionConnectors = state.collisionConnectors || {};
                                    state.collisionConnectors[employeeId] = L.polyline(
                                        [connectorOrigin, animatedPosition],
                                        {
                                            color: marker._adminVisualKey?.startsWith('within') ? '#198754' : '#dc3545',
                                            weight: 2,
                                            opacity: .72,
                                            dashArray: '3,4',
                                            lineCap: 'round'
                                        }
                                    ).addTo(state.map);
                                } else {
                                    state.collisionConnectors[employeeId].setLatLngs([
                                        connectorOrigin,
                                        animatedPosition
                                    ]);
                                }
                            } else if (state.collisionConnectors?.[employeeId]) {
                                try { state.map.removeLayer(state.collisionConnectors[employeeId]); } catch { }
                                delete state.collisionConnectors[employeeId];
                            }

                            if (state.journeyLabels?.[employeeId]) {
                                state.journeyLabels[employeeId]
                                    .setLatLng(animatedPosition);
                            }

                            // Smoother road route line connection without crossing to office
                            window.payrollUpdateRouteEmployeeEndpoint(state.roadRouteLines?.[employeeId], animatedPosition);
                            window.payrollUpdateRouteEmployeeEndpoint(state.roadRouteCasings?.[employeeId], animatedPosition);

                            // Follow the animated marker position in real-time so the camera
                            // tracks smooth motion rather than snapping to the raw GPS target.
                            // Auto-zoom-out if the employee drifts near or off the visible edge.
                            if (state.followSelected && Number(state.lastSelectedId) === employeeId && Number(state.lastSelectedId) > 0) {
                                try {
                                    const map = state.map;
                                    const bounds = map.getBounds();
                                    const latSpan = bounds.getNorth() - bounds.getSouth();
                                    const lonSpan = bounds.getEast() - bounds.getWest();
                                    const margin = 0.18;
                                    const animLat = Number(Array.isArray(animatedPosition) ? animatedPosition[0] : (animatedPosition?.lat ?? 0));
                                    const animLng = Number(Array.isArray(animatedPosition) ? animatedPosition[1] : (animatedPosition?.lng ?? 0));
                                    const nearEdge =
                                        animLat < bounds.getSouth() + latSpan * margin ||
                                        animLat > bounds.getNorth() - latSpan * margin ||
                                        animLng < bounds.getWest()  + lonSpan * margin ||
                                        animLng > bounds.getEast()  - lonSpan * margin;
                                    const animTarget = [animLat, animLng];
                                    if (nearEdge) {
                                        // Employee approaching viewport edge — zoom out one level to keep them centred
                                        const currentZoom = map.getZoom();
                                        const targetZoom = Math.max(currentZoom - 1, 10);
                                        map.flyTo(animTarget, targetZoom, { animate: true, duration: 0.5 });
                                    } else {
                                        map.panTo(animTarget, { animate: true, duration: 0.3 });
                                    }
                                } catch { }
                            }
                        }
                        catch { }
                    }
                );
            }
            else {
                marker.setLatLng(displayTarget);
                // Fallback synchronous follow when animation is unavailable
                if (state.followSelected && Number(state.lastSelectedId) === employeeId && Number(state.lastSelectedId) > 0) {
                    try { state.map.panTo(displayTarget, { animate: true, duration: 0.7 }); } catch { }
                }
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
            // actual GPS coordinate, without overwriting the office destination.
            window.payrollUpdateRouteEmployeeEndpoint(state.roadRouteLines?.[employeeId], target);
            window.payrollUpdateRouteEmployeeEndpoint(state.roadRouteCasings?.[employeeId], target);
            window.refreshAdminLiveMapSummary(mapId);
        }
        catch (error) {
            console.warn(
                'Admin live realtime marker update failed:',
                error
            );
        }
    };

    const geoSettingsHandler = function (event) {
        try {
            const data = event?.detail;
            if (!data) return;
            const state = window.adminLiveMaps?.[mapId];
            if (!state || !state.map) return;

            const lat = Number(data.OfficeLatitude ?? data.officeLatitude);
            const lng = Number(data.OfficeLongitude ?? data.officeLongitude);
            const rad = Number(data.GeoRadiusMeters ?? data.geoRadiusMeters);

            if (Number.isFinite(lat) && Number.isFinite(lng) && lat !== 0 && lng !== 0) {
                state.office = [lat, lng];
                if (state.officeMarker) {
                    state.officeMarker.setLatLng(state.office);
                }
            }

            if (Number.isFinite(rad) && rad > 0) {
                state.lastOfficeRadius = rad;
                if (!state.circle && state.map && state.office) {
                    state.circle = L.circle(state.office, {
                        radius: rad,
                        color: '#0d6efd',
                        weight: 2,
                        opacity: 0.72,
                        fillColor: '#0d6efd',
                        fillOpacity: 0.08,
                        interactive: false,
                        bubblingMouseEvents: false
                    }).addTo(state.map);
                } else if (state.circle) {
                    state.circle.setRadius(rad);
                    if (state.office) {
                        state.circle.setLatLng(state.office);
                    }
                }
            }

            // Immediately re-evaluate all markers on the map against the new radius
            if (state.markers) {
                Object.keys(state.markers).forEach(function (empId) {
                    const marker = state.markers[empId];
                    if (!marker) return;
                    const markerLatLng = marker.getLatLng();
                    if (!markerLatLng) return;

                    const empData = state.liveData ? state.liveData[empId] : null;
                    const target = [markerLatLng.lat, markerLatLng.lng];
                    if (Array.isArray(state.office) && state.lastOfficeRadius > 0 && typeof window.payrollHaversineMeters === 'function') {
                        const dist = window.payrollHaversineMeters(target, state.office);
                        const within = dist <= state.lastOfficeRadius + 1;
                        if (empData) empData.isWithinAllowedRadius = within;
                        marker._adminWithinRange = within;

                        const metaName = String(marker._adminEmployeeName || 'Employee').trim();
                        const metaParts = metaName.split(/\s+/).filter(Boolean);
                        const metaInitials = metaParts.length === 1 ? metaParts[0].slice(0, 1) : (metaParts[0][0] + metaParts[metaParts.length - 1][0]);
                        const metaSpeedMps = Number(empData?.speedMps || marker._speedMps || 0);
                        const metaSpeedKmh = metaSpeedMps * 3.6;
                        const useSpeedIcons = !!(window.payrollCompanySettings && window.payrollCompanySettings.useSpeedBasedMarkers);
                        const speedTier = useSpeedIcons ? window.payrollGetSpeedEmoji(metaSpeedKmh) : '';
                        const visualKey = (within ? 'within' : 'outside') + '|active|' + speedTier;

                        if (marker._adminVisualKey !== visualKey) {
                            const html = window.payrollMakeAdminMarkerHtml(metaInitials, within ? 'within' : 'outside', '', metaSpeedKmh, metaName, useSpeedIcons);
                            marker.setIcon(L.divIcon({
                                className: 'payroll-user-marker',
                                html: html,
                                iconSize: [46, 54],
                                iconAnchor: [23, 54]
                            }));
                            marker._adminVisualKey = visualKey;
                        }
                    }
                });
            }

            if (typeof window.refreshAdminLiveMapSummary === 'function') {
                window.refreshAdminLiveMapSummary(mapId);
            }
        } catch (e) {
            console.warn('Admin live map geo-settings event update failed:', e);
        }
    };

    window.__adminLiveRealtime[mapId] = {
        handler: handler,
        geoSettingsHandler: geoSettingsHandler
    };

    window.addEventListener(
        'location-data-changed',
        handler
    );

    window.addEventListener(
        'geo-settings-changed',
        geoSettingsHandler
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

    try {
        if (registry[mapId].geoSettingsHandler) {
            window.removeEventListener(
                'geo-settings-changed',
                registry[mapId].geoSettingsHandler
            );
        }
    }
    catch { }

    delete registry[mapId];
};

/**
 * Returns an emoji icon appropriate for the given speed in km/h.
 * Used for speed-based marker icons (company setting: UseSpeedBasedMarkers).
 */
window.payrollGetSpeedEmoji = function (speedKmh) {
    const s = Number(speedKmh) || 0;
    if (s < 1)   return '🧍'; // Stopped
    if (s < 5)   return '🚶'; // Walking
    if (s < 30)  return '🛵'; // Very slow / residential
    if (s < 60)  return '🚗'; // City driving
    if (s < 90)  return '🚕'; // Highway
    if (s < 120) return '🚙'; // Fast highway
    if (s < 150) return '🏎️'; // Racing / very fast
    if (s < 200) return '🚄'; // Train speed
    if (s < 500) return '✈️'; // Aircraft
    if (s < 1000) return '🚀'; // Rocket
    return '☄️'; // Hypersonic
};

/**
 * Returns the speed tier label (for tooltip display).
 */
window.payrollGetSpeedLabel = function (speedKmh) {
    const s = Number(speedKmh) || 0;
    if (s < 1)   return 'Stopped';
    if (s < 5)   return 'Walking';
    if (s < 30)  return 'Slow';
    if (s < 60)  return 'City';
    if (s < 90)  return 'Highway';
    if (s < 120) return 'Fast';
    if (s < 150) return 'Racing';
    return `${s.toFixed(0)} km/h`;
};

/**
 * Builds the inner HTML for an employee map marker div.
 * When useSpeedIcons=true, shows speed emoji + name label.
 * When false, shows initials avatar (original behaviour).
 */
window.payrollMakeAdminMarkerHtml = function (initials, avatarClass, statusDotClass, speedKmh, employeeName, useSpeedIcons) {
    const safeName = window.escapeAdminHtml ? window.escapeAdminHtml(employeeName) : employeeName;
    const safeInitials = (window.escapeAdminHtml ? window.escapeAdminHtml(initials) : initials).toUpperCase();

    if (useSpeedIcons) {
        const emoji = window.payrollGetSpeedEmoji(speedKmh);
        return '<div class="payroll-map-user payroll-map-user-' + avatarClass + ' payroll-map-user-speed">' +
               '<span class="payroll-map-user-speed-emoji" aria-hidden="true">' + emoji + '</span>' +
               '<span class="payroll-map-user-speed-name">' + safeName + '</span>' +
               '<span class="payroll-map-user-status' + statusDotClass + '"></span>' +
               '</div>';
    }
    // Default: initials avatar + persistent name label
    return '<div class="payroll-map-user payroll-map-user-' + avatarClass + '">' +
           '<span class="payroll-map-user-initials">' + safeInitials + '</span>' +
           '<span class="payroll-map-user-status' + statusDotClass + '"></span>' +
           '</div>' +
           '<div class="payroll-map-user-namelabel">' + safeName + '</div>';
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
        dotNetRef,
        useSpeedMarkersFlag
    ) {
        if (typeof useSpeedMarkersFlag === 'boolean') {
            window.payrollCompanySettings = window.payrollCompanySettings || {};
            window.payrollCompanySettings.useSpeedBasedMarkers = useSpeedMarkersFlag;
        }

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
                "Admin live map: office GPS coordinates are not available yet; waiting for Company Settings."
            );
            return;
        }

        try {
            await window.loadPayrollLeaflet();

            const office = [
                parsedOfficeLat,
                parsedOfficeLng
            ];

            if (typeof window.registerAdminLiveLocationRealtime === 'function') {
                try {
                    window.registerAdminLiveLocationRealtime(mapId);
                } catch { }
            }

            const liveStaff =
                Array.isArray(staff)
                    ? staff
                    : [];

            // Keep a presentation-only cache of exact live GPS coordinates so
            // direct realtime updates can calculate collision offsets against
            // all currently visible employees rather than only the employee
            // that generated the latest event.
            const existingStateForCollision = window.adminLiveMaps[mapId];
            if (existingStateForCollision) {
                existingStateForCollision.liveData = {};
                liveStaff.forEach(function (x) {
                    const id = Number(x.employeeId);
                    const lat = Number(x.latitude);
                    const lng = Number(x.longitude);
                    const status = String(x.status || '').toLowerCase();
                    const sessionId = String(x.sessionId || x.SessionId || x.employeeId || '').trim();
                    if (id > 0 && Number.isFinite(lat) && Number.isFinite(lng) && sessionId) {
                        existingStateForCollision.liveData[id] = {
                            employeeId: id,
                            latitude: lat,
                            longitude: lng,
                            speedMps: Number(x.speedMps ?? x.SpeedMps ?? 0),
                            isWithinAllowedRadius: Boolean(x.isWithinAllowedRadius ?? x.IsWithinAllowedRadius)
                        };
                    }
                });
            }

            // All live employees remain on the map so the radar maintains full staff awareness.
            // When an employee is selected, they are highlighted and focused with the company office,
            // while remaining employees stay visible without blinking or disappearing.
            const visibleStaff = liveStaff;

            const displayPoints = [];

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

                // Keep Leaflet's zoom +/- control outside the map viewport.
                // The Web Admin dashboard renders a dedicated bottom control
                // strip below the map, so the control never covers roads,
                // employee markers, or the map's left edge.
                const bottomZoomHost =
                    document.getElementById(mapId + '-zoom-controls');

                if (bottomZoomHost && map.zoomControl) {
                    bottomZoomHost.appendChild(
                        map.zoomControl.getContainer()
                    );
                }

                const standardTileLayer = L.tileLayer(
                    'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
                    {
                        maxZoom: 19,
                        attribution:
                            '© OpenStreetMap contributors'
                    }
                ).addTo(map);

                // Dark mode uses the same OpenStreetMap data with a local CSS
                // presentation layer. This removes the CARTO API-key dependency.
                const darkTileLayer = L.tileLayer(
                    'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
                    {
                        maxZoom: 19,
                        className: 'payroll-dark-osm-tiles',
                        attribution:
                            '© OpenStreetMap contributors'
                    }
                );

                const satelliteTileLayer = L.tileLayer(
                    'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
                    {
                        maxZoom: 19,
                        attribution: 'Tiles © Esri'
                    }
                );

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
                    baseLayers: {
                        standard: standardTileLayer,
                        dark: darkTileLayer,
                        satellite: satelliteTileLayer
                    },
                    baseLayer: 'standard',
                    userSelectedLayer: false,
                    circle: null,
                    markers: {},
                    lines: {},
                    trails: {},
                    trailPoints: {},
                    labels: {},
                    journeyLabels: {},
                    collisionConnectors: {},
                    stayHubMarkers: {},
                    lastOfficeRadius: 0,
                    historyRoute: null,
                    historyMarkers: [],
                    historyStartMarker: null,
                    historyEndMarker: null,
                    hasInitialFit: false,
                    initialFitStaffCount: 0,
                    lastStaffSignature: '',
                    lastSelectedId: 0,
                    followSelected: false,
                    isPlayback: !!isPlayback,
                    lastLocationAt: {},
                    routeStates: {},
                    roadRouteLines: {},
                    roadRouteCasings: {},
                    journeyStartedAt: {},
                    realtimeLastAt: {},
                    markerSessions: {},
                    liveData: {},
                    office: office.slice(),
                    lastOfficeRadius: 0,
                    liveSummaryTimer: null
                };

                window.adminLiveMaps[mapId] =
                    state;

                map.on('click', function (e) {
                    if (e.originalEvent?._stoppedByMarker) return;
                    if (state.markerDotNetRef && Number(state.lastSelectedId) > 0) {
                        try {
                            state.markerDotNetRef.invokeMethodAsync('SelectEmployeeFromMap', 0);
                        } catch (err) { }
                    }
                });

                window.ensureAdminLiveMapLayout(mapId);
                if (!state.liveSummaryTimer) {
                    state.liveSummaryTimer = setInterval(function () {
                        window.refreshAdminLiveMapSummary(mapId);
                    }, 5000);
                }

                try {
                    if (!state._controlsToggleBound) {
                        const mapContainer = map.getContainer();
                        mapContainer.addEventListener('click', function (e) {
                            if (e.target && (e.target.closest('.payroll-user-marker') || e.target.closest('.payroll-map-floating-nav') || e.target.closest('.payroll-premium-map-ui'))) return;
                            const wrapper = mapContainer.parentElement;
                            if (!wrapper) return;
                            wrapper.classList.toggle('controls-open');
                        });
                        state._controlsToggleBound = true;
                    }
                } catch { }
            }

            state.officeMarker
                .setLatLng(office);

            const staffIds =
                new Set(
                    visibleStaff.map(
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

                    if (staffIds.has(employeeId)) {
                        if (state.pendingRemoval) {
                            delete state.pendingRemoval[employeeId];
                        }
                        return;
                    }

                    // 20-second grace period prevents temporary network gaps or
                    // partial SSE packets from blinking/removing live markers.
                    state.pendingRemoval = state.pendingRemoval || {};
                    const now = Date.now();
                    if (!state.pendingRemoval[employeeId]) {
                        state.pendingRemoval[employeeId] = now;
                        return;
                    }
                    if (now - state.pendingRemoval[employeeId] < 20000) {
                        return;
                    }
                    delete state.pendingRemoval[employeeId];

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
                    delete state.markerSessions[id];
                }
            );

            state.office = office.slice();

            const staffSignature =
                visibleStaff
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
                    visibleStaff,
                    selectedId
                );

            if (typeof window.payrollUpdateAdminStayHubMarkers === 'function') {
                window.payrollUpdateAdminStayHubMarkers(state, markerDisplayPositions._clusters);
            }

            visibleStaff.forEach(
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
                        !Number.isFinite(lng)
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

                    displayPoints.push({ staff: x, pos: L.latLng(displayPosition) });

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
                        // Suppress GPS teleport spikes (> 1500m jumps between consecutive samples)
                        const jumpDist = previousPoint ? window.payrollHaversineMeters(previousPoint, position) : 0;
                        if (!previousPoint || (jumpDist > 1 && jumpDist < 2500)) {
                            points.push(position);
                            if (points.length > 80) {
                                points.shift();
                            }
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
                    state.markerSessions[employeeId] = String(x.sessionId || x.SessionId || '');

                    // Speed-based icon support
                    const speedMps = Number(x.speedMps) || 0;
                    const speedKmh = speedMps * 3.6;
                    const useSpeedIcons = !!(window.payrollCompanySettings && window.payrollCompanySettings.useSpeedBasedMarkers);

                    const markerHtml = window.payrollMakeAdminMarkerHtml(
                        initials, avatarClass, statusDotClass, speedKmh, rawName, useSpeedIcons
                    );

                    const icon =
                        L.divIcon({
                            className: 'payroll-user-marker',
                            html: markerHtml,
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
                            state.markers[employeeId].on('click', function (e) {
                                if (e?.originalEvent) e.originalEvent._stoppedByMarker = true;
                                try {
                                    dotNetRef.invokeMethodAsync('SelectEmployeeFromMap', employeeId);
                                } catch (e) { }
                            });
                            state.markerDotNetRef = dotNetRef;
                        }

                        markerCreated = true;

                        if (typeof window.payrollKeepAdminTooltipVisible === 'function') {
                            window.payrollKeepAdminTooltipVisible(
                                state.map,
                                state.markers[employeeId]
                            );
                        }
                    }
                    else {
                        const markerRef =
                            state.markers[employeeId];

                        // Include speed tier in visual key so marker refreshes on speed change
                        const speedTier = useSpeedIcons ? window.payrollGetSpeedEmoji(speedKmh) : '';
                        const iconVisualKey =
                            (withinRange ? 'within' : 'outside') +
                            '|' + status +
                            '|' + speedTier;

                        if (markerRef._adminVisualKey !== iconVisualKey) {
                            markerRef.setIcon(icon);
                            markerRef._adminVisualKey = iconVisualKey;
                        }


                        // The map may have been created before the Blazor
                        // reference was available. Ensure the click handler
                        // exists on every update without stacking handlers.
                        if (dotNetRef && state.markerDotNetRef !== dotNetRef) {
                            try {
                                state.markers[employeeId].off('click');
                            } catch (e) { }
                            state.markers[employeeId].on('click', function (e) {
                                if (e?.originalEvent) e.originalEvent._stoppedByMarker = true;
                                try {
                                    dotNetRef.invokeMethodAsync('SelectEmployeeFromMap', employeeId);
                                } catch (e) { }
                            });
                            state.markerDotNetRef = dotNetRef;
                        }

                        if (typeof window.payrollKeepAdminTooltipVisible === 'function') {
                            window.payrollKeepAdminTooltipVisible(
                                state.map,
                                state.markers[employeeId]
                            );
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
                                62000,
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
                                        const connectorOrigin = (displayItem && Number.isFinite(displayItem.centerLat) && displayItem.isClustered)
                                            ? [displayItem.centerLat, displayItem.centerLng]
                                            : position;
                                        state.collisionConnectors[employeeId].setLatLngs([
                                            connectorOrigin,
                                            animatedPosition
                                        ]);
                                    }

                                    if (state.journeyLabels[employeeId]) {
                                        state.journeyLabels[employeeId].setLatLng(animatedPosition);
                                    }

                                    // Update road route endpoint to match visual marker motion without creating a diagonal chord to the office
                                    window.payrollUpdateRouteEmployeeEndpoint(state.roadRouteLines?.[employeeId], animatedPosition);
                                    window.payrollUpdateRouteEmployeeEndpoint(state.roadRouteCasings?.[employeeId], animatedPosition);

                                    if (state.labels[employeeId]) {
                                        state.labels[employeeId].setLatLng(
                                            window.getAdminLineMidpoint(
                                                office,
                                                position
                                            )
                                        );
                                    }

                                    // Follow the animated marker in real-time with auto-zoom-out when near the edge.
                                    if (isSelected && state.followSelected && !isPlayback && Number(selectedId) > 0) {
                                        try {
                                            const map = state.map;
                                            const bounds = map.getBounds();
                                            const latSpan = bounds.getNorth() - bounds.getSouth();
                                            const lonSpan = bounds.getEast() - bounds.getWest();
                                            const margin = 0.18;
                                            const nearEdge =
                                                animatedPosition.lat < bounds.getSouth() + latSpan * margin ||
                                                animatedPosition.lat > bounds.getNorth() - latSpan * margin ||
                                                animatedPosition.lng < bounds.getWest()  + lonSpan * margin ||
                                                animatedPosition.lng > bounds.getEast()  - lonSpan * margin;
                                            if (nearEdge) {
                                                const targetZoom = Math.max(map.getZoom() - 1, 10);
                                                map.flyTo(animatedPosition, targetZoom, { animate: true, duration: 0.5 });
                                            } else {
                                                map.panTo(animatedPosition, { animate: true, duration: 0.3 });
                                            }
                                        } catch { }
                                    }
                                }
                                catch { }
                            }
                        );
                    }

                    state.markers[employeeId]._adminEmployeeName = rawName;
                    state.markers[employeeId]._adminWithinRange = withinRange;
                    state.markerSessions[employeeId] = String(x.sessionId || x.SessionId || '');

                    if (markerCreated) {
                        state.lastLocationAt[employeeId] = Date.now();
                    }

                    // Professional road-snapped travelled route for the selected employee ("which way he came").
                    // Follows streets cleanly (Swiggy/Zomato style) with green fill and white casing; never sharp straight lines.
                    if (isSelected && points.length > 1) {
                        state.trailCasings = state.trailCasings || {};
                        state.travelledRouteState = state.travelledRouteState || {};

                        if (!state.trailCasings[employeeId]) {
                            state.trailCasings[employeeId] = L.polyline(
                                window.payrollGenerateSmoothSpline(points),
                                {
                                    color: '#ffffff',
                                    weight: 8,
                                    opacity: 0.85,
                                    lineCap: 'round',
                                    lineJoin: 'round'
                                }
                            ).addTo(state.map);
                        }

                        if (!state.trails[employeeId]) {
                            state.trails[employeeId] = L.polyline(
                                window.payrollGenerateSmoothSpline(points),
                                {
                                    color: '#10b981',
                                    weight: 5,
                                    opacity: 0.98,
                                    lineCap: 'round',
                                    lineJoin: 'round'
                                }
                            ).addTo(state.map);
                        }

                        const trs = state.travelledRouteState[employeeId] || {};
                        state.travelledRouteState[employeeId] = trs;
                        const lastPoint = points[points.length - 1];
                        const lastRouted = trs.lastPoint;
                        const hasMovedEnough = !lastRouted || window.payrollHaversineMeters(lastRouted, lastPoint) >= 20 || trs.lastCount !== points.length;

                        if (hasMovedEnough && !trs.fetching) {
                            trs.fetching = true;
                            trs.lastPoint = [lastPoint[0], lastPoint[1]];
                            trs.lastCount = points.length;

                            window.payrollFetchMultiPointRoadRoute(points)
                                .then(function (result) {
                                    if (!result?.geometry || Number(selectedId) !== employeeId) return;
                                    if (state.trailCasings[employeeId]) {
                                        state.trailCasings[employeeId].setLatLngs(result.geometry);
                                    }
                                    if (state.trails[employeeId]) {
                                        state.trails[employeeId].setLatLngs(result.geometry);
                                    }
                                })
                                .catch(function () { })
                                .finally(function () { trs.fetching = false; });
                        }
                    }
                    else {
                        if (state.trails[employeeId]) {
                            try { state.map.removeLayer(state.trails[employeeId]); } catch (e) { }
                            delete state.trails[employeeId];
                        }
                        if (state.trailCasings?.[employeeId]) {
                            try { state.map.removeLayer(state.trailCasings[employeeId]); } catch (e) { }
                            delete state.trailCasings[employeeId];
                        }
                        if (state.travelledRouteState?.[employeeId]) {
                            delete state.travelledRouteState[employeeId];
                        }
                    }

                    if (hasCollisionOffset) {
                        const connectorOrigin = (displayItem && Number.isFinite(displayItem.centerLat) && displayItem.isClustered)
                            ? [displayItem.centerLat, displayItem.centerLng]
                            : position;
                        if (!state.collisionConnectors[employeeId]) {
                            state.collisionConnectors[employeeId] = L.polyline(
                                [connectorOrigin, displayPosition],
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
                                connectorOrigin,
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
                            color: '#10b981',
                            weight: 5,
                            opacity: Number(selectedId) > 0 && !isSelected ? 0 : (isSelected ? .98 : 0)
                        });
                    }
                    if (state.trailCasings?.[employeeId]) {
                        state.trailCasings[employeeId].setStyle({
                            color: '#ffffff',
                            weight: 8,
                            opacity: Number(selectedId) > 0 && !isSelected ? 0 : (isSelected ? .85 : 0)
                        });
                    }

                    if (state.roadRouteLines[employeeId]) {
                        state.roadRouteLines[employeeId].setStyle({
                            color: '#1688ff',
                            weight: isSelected ? 5 : 3.5,
                            opacity: Number(selectedId) > 0 && !isSelected ? 0 : (isSelected ? .95 : .6)
                        });
                    }
                    if (state.roadRouteCasings[employeeId]) {
                        state.roadRouteCasings[employeeId].setStyle({
                            color: '#ffffff',
                            weight: isSelected ? 8 : 6,
                            opacity: Number(selectedId) > 0 && !isSelected ? 0 : .65
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

                    // Show hover tooltip for every employee marker: name, role, speed, status.
                    // This eliminates the "which employee is this?" confusion without cluttering the map.
                    try {
                        const safeRole = window.escapeAdminHtml(String(x.role || 'Staff'));
                        const safeDisplayName = window.escapeAdminHtml(rawName);
                        const speedLabel = window.payrollGetSpeedLabel(speedKmh);
                        const speedEmoji = window.payrollGetSpeedEmoji(speedKmh);
                        const rangeClass = withinRange ? 'within' : 'outside';
                        const rangeLabel = withinRange ? '✅ In range' : '🔴 Outside';

                        const hoverHtml = window.payrollMakeAdminMarkerTooltipHtml(
                            initials,
                            safeDisplayName,
                            rangeClass,
                            rangeLabel,
                            safeRole,
                            speedEmoji,
                            speedLabel,
                            state.markers[employeeId]._currentAddress
                        );

                        const currentTooltip = state.markers[employeeId].getTooltip();
                        if (currentTooltip) {
                            state.markers[employeeId].setTooltipContent(hoverHtml);
                        } else {
                            state.markers[employeeId].bindTooltip(hoverHtml, {
                                permanent: false,
                                direction: 'top',
                                offset: [0, -12],
                                sticky: false,
                                className: 'admin-live-hover-tooltip'
                            });
                        }

                        // Asynchronously resolve and update location address/name
                        if (typeof window.payrollFetchMarkerAddress === 'function') {
                            window.payrollFetchMarkerAddress(state.markers[employeeId], lat, lng, function (addr) {
                                try {
                                    if (state.markers[employeeId]?.getTooltip()) {
                                        state.markers[employeeId].setTooltipContent(window.payrollMakeAdminMarkerTooltipHtml(
                                            initials,
                                            safeDisplayName,
                                            rangeClass,
                                            rangeLabel,
                                            safeRole,
                                            speedEmoji,
                                            speedLabel,
                                            addr
                                        ));
                                    }
                                } catch { }
                            });
                        }

                        // Store speed for realtime bridge updates
                        state.markers[employeeId]._speedMps = speedMps;
                        state.markers[employeeId]._role = String(x.role || 'Staff');

                        if (state.markers[employeeId].isPopupOpen?.()) {
                            state.markers[employeeId].closePopup();
                        }
                    } catch { }


                    // Road routing is a selected-employee detail only.
                    // The default live map shows every employee marker but does
                    // not draw employee-to-office routes. This keeps the map
                    // readable and avoids a route line for every employee.
                    if (isSelected) {
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

                        const routeState = state.routeStates[employeeId];
                        const routeNow = Date.now();

                        routeState.lastRawPosition = position.slice();
                        routeState.lastRawPositionAt = routeNow;

                        window.payrollRequestJourneyRoute(
                            routeState,
                            position,
                            office,
                            { minMoveMeters: 25, minIntervalMs: 30000 }
                        ).then(function (route) {
                            if (!route || !state.markers[employeeId] || Number(selectedId) !== employeeId) return;
                            const remaining = route.distanceMeters || window.payrollHaversineMeters(state.markers[employeeId].getLatLng(), office);
                            state.roadRouteCasings[employeeId]?.setLatLngs(route.geometry);
                            state.roadRouteLines[employeeId]?.setLatLngs(route.geometry);
                            const routeDistance = window.payrollFormatRouteDistance(remaining);
                            const eta = window.payrollFormatRouteDuration(route.durationSeconds);

                            const distanceElement = document.querySelector(`[data-selected-route-distance="${employeeId}"]`);
                            if (distanceElement) distanceElement.innerText = routeDistance;

                            const etaElement = document.querySelector(`[data-selected-eta="${employeeId}"]`);
                            if (etaElement) etaElement.innerText = eta;

                            const hoverSpeed = window.payrollFormatSpeed(x.speedMps || state.markers[employeeId]._speedMps || 0);
                            const hoverWithin = Boolean(x.isWithinAllowedRadius);

                            if (state.markers[employeeId].getTooltip()) {
                                state.markers[employeeId].setTooltipContent(
                                    window.payrollCreateAdminTooltipHtml(
                                        x, initials.toUpperCase(), hoverWithin, distance, routeDistance, eta, hoverSpeed
                                    )
                                );
                            }
                        }).catch(function () { });
                    } else {
                        // Unselected employees must never retain an old route.
                        try { state.roadRouteLines[employeeId]?.remove(); } catch (e) { }
                        try { state.roadRouteCasings[employeeId]?.remove(); } catch (e) { }
                        delete state.roadRouteLines[employeeId];
                        delete state.roadRouteCasings[employeeId];
                        try { state.routeStates[employeeId]?.controller?.abort(); } catch (e) { }
                        delete state.routeStates[employeeId];
                    }

                    // Route generation is intentionally handled only by the
                    // selected-employee branch above. Unselected employees never
                    // recreate or dereference a deleted route state.
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

            state.office = office.slice();
            state.lastOfficeRadius = effectiveRadius;

            state.lastOfficeRadius = effectiveRadius;

            // Keep the geofence above route/trail overlays and below
            // employee/office markers, making it visible at all zooms.
            try {
                state.circle.bringToFront();
            } catch { }

            // --------------------------------------------------------
            // CAMERA / FIT-BOUNDS RULES (3 scenarios):
            //
            // 1. No employee selected (selectedId == 0):
            //    → fitBounds(all employees + office) ONLY on first init.
            //    → On subsequent GPS updates (same employees moving), DO NOT
            //      re-center. Moving markers should not pull the camera.
            //    → When an employee disappears (membershipChanged), also do NOT
            //      re-fit — the admin was already viewing a stable overview and
            //      losing one employee must not yank the camera.
            //
            // 2. One employee selected (selectedId > 0):
            //    → fitBounds([selectedEmp, office]) ONLY when the selection
            //      itself changes (lastSelectedId !== selectedId).
            //    → On subsequent GPS updates for that employee, the panTo in
            //      the per-marker loop already handles following (guarded by
            //      followSelected flag). Do not re-fit here.
            //
            // 3. Manual "Fit All" / "Recenter" toolbar actions are handled by
            //    handlePremiumAdminMapAction and operate independently.
            // --------------------------------------------------------
            const selId = Number(selectedId);
            const selectionChanged = state.lastSelectedId !== selId;

            // Initial fit: fits all employees + office when the map first renders OR
            // when live staff first arrive (if initial fit happened before staff loaded).
            const needsInitialFit = !state.hasInitialFit || (state.initialFitStaffCount === 0 && liveStaff.length > 0 && selId === 0);

            if (needsInitialFit) {
                const initPoints = [office];
                liveStaff.forEach(function (x) {
                    const lat = Number(x.latitude);
                    const lng = Number(x.longitude);
                    if (Number.isFinite(lat) && Number.isFinite(lng)) {
                        initPoints.push([lat, lng]);
                    }
                });
                if (initPoints.length > 1) {
                    const bounds = L.latLngBounds(initPoints);
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
                state.initialFitStaffCount = liveStaff.length;
            } else if (selectionChanged) {
                // Selection changed: fit the newly selected employee + office,
                // or fit all when deselecting back to "All staff".
                const selPoints = [office];
                if (selId > 0) {
                    // Scope camera to the selected employee + office.
                    const selStaff = liveStaff.find(function (x) {
                        return Number(x.employeeId) === selId;
                    });
                    if (selStaff) {
                        const lat = Number(selStaff.latitude);
                        const lng = Number(selStaff.longitude);
                        if (Number.isFinite(lat) && Number.isFinite(lng)) {
                            selPoints.push([lat, lng]);
                        }
                    }
                    if (selPoints.length > 1) {
                        const bounds = L.latLngBounds(selPoints);
                        if (bounds.isValid()) {
                            state.map.fitBounds(bounds, {
                                padding: [60, 60],
                                maxZoom: 17,
                                animate: true
                            });
                        }
                    } else if (selPoints.length === 1) {
                        state.map.setView(selPoints[0], 15, { animate: true });
                    }
                } else {
                    // Deselected back to all-staff: show all employees + office.
                    const allPoints = [office];
                    liveStaff.forEach(function (x) {
                        const lat = Number(x.latitude);
                        const lng = Number(x.longitude);
                        if (Number.isFinite(lat) && Number.isFinite(lng)) {
                            allPoints.push([lat, lng]);
                        }
                    });
                    if (allPoints.length > 1) {
                        const bounds = L.latLngBounds(allPoints);
                        if (bounds.isValid()) {
                            state.map.fitBounds(bounds, {
                                padding: [60, 60],
                                maxZoom: 17,
                                animate: true
                            });
                        }
                    } else {
                        state.map.setView(office, 15);
                    }
                    state.followSelected = false;
                }
            }
            // NOTE: membershipChanged (employee going online/offline) intentionally
            // does NOT trigger fitBounds here. The admin's current viewport must
            // remain stable when employees appear or disappear from the live list.

            state.lastStaffSignature = staffSignature;
            state.lastSelectedId = Number(selectedId);
            state.isPlayback = !!isPlayback;
            state.liveStaff = visibleStaff;
            state.allLiveStaff = liveStaff;
            state.office = office;
            state.selectedId = Number(selectedId);

            // Presentation-only premium controls. They operate on the already
            // rendered Leaflet state and never alter GPS, attendance, sessions,
            // persistence, or the existing Blazor flow.
            if (typeof window.enhanceAdminLiveMap === 'function') {
                window.enhanceAdminLiveMap(mapId, office, visibleStaff, selectedId);
            }
        } catch (error) {
            console.error("Admin live map update failed:", error);
        }
    };

// ============================================================
// PREMIUM ADMIN LIVE MAP CONTROLS
// ============================================================
// Presentation-only layer. Existing GPS/Blazor state remains authoritative.
// Provides industry-style map controls without changing application flow.

window.enhanceAdminLiveMap = function (mapId, office, staff, selectedId) {
    try {
        const state = window.adminLiveMaps?.[mapId];
        if (!state?.map) return;

        const map = state.map;
        const container = map.getContainer();
        state.liveStaff = Array.isArray(staff) ? staff : [];
        state.office = office;
        state.selectedId = Number(selectedId) || 0;
        state.uiFilter = state.uiFilter || 'all';
        state.uiSearch = state.uiSearch || '';
        state.geofenceVisible = state.geofenceVisible !== false;
        state.trailsVisible = state.trailsVisible !== false;
        state.followSelected = !!state.followSelected;
        state.baseLayer = state.baseLayer || 'standard';

        if (!state.premiumControls) {
            const esc = window.payrollEscapeHtml || (v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;' }[c])));
            if (!window.__payrollAdminMapFullscreenBound) {
                const exitAdminFullscreen = function () {
                    const isFs = !!document.fullscreenElement;
                    if (!isFs) {
                        document.querySelectorAll('.payroll-map-fullscreen').forEach(function (node) {
                            node.classList.remove('payroll-map-fullscreen');
                        });
                        document.body.classList.remove('payroll-fullscreen-active');
                    }
                    document.querySelectorAll('.payroll-map-commandbar.is-fullscreen-hidden').forEach(function (bar) {
                        if (!isFs) {
                            bar.classList.remove('is-fullscreen-hidden');
                        }
                    });
                    Object.values(window.adminLiveMaps || {}).forEach(function (state) {
                        if (!state?.map) return;
                        const container = state.map.getContainer();
                        const fullscreenHost = container.closest('.admin-map-wrapper') || container;
                        const inFs = isFs && (document.fullscreenElement === fullscreenHost || document.fullscreenElement === container);

                        if (!inFs) {
                            container.classList.remove('payroll-map-fullscreen');
                            fullscreenHost.classList.remove('payroll-map-fullscreen');
                            if (state.fullscreenSelectedRail?.parentElement) state.fullscreenSelectedRail.remove();
                            state.fullscreenSelectedRail = null;
                            window.payrollUpdateAdminMapFullscreenUI?.(state.mapId, false);
                        } else {
                            const rail = state.selectedRailElement || document.querySelector('[data-admin-selected-rail="' + CSS.escape(String(state.mapId || '')) + '"]');
                            if (rail) {
                                window.syncAdminSelectedRailFullscreen?.(state, rail, state.selectedId);
                            }
                            window.payrollUpdateAdminMapFullscreenUI?.(state.mapId, true);
                        }
                        setTimeout(function () { try { state.map.invalidateSize({ animate: false }); } catch { } }, 50);
                        setTimeout(function () { try { state.map.invalidateSize({ animate: false }); } catch { } }, 200);
                        setTimeout(function () { try { state.map.invalidateSize({ animate: false }); } catch { } }, 450);
                    });
                };

                document.addEventListener('fullscreenchange', exitAdminFullscreen);
                document.addEventListener('webkitfullscreenchange', exitAdminFullscreen);
                document.addEventListener('mozfullscreenchange', exitAdminFullscreen);
                document.addEventListener('MSFullscreenChange', exitAdminFullscreen);

                document.addEventListener('keydown', function (e) {
                    if (e.key === 'Escape' || e.key === 'Esc' || e.keyCode === 27) {
                        const fsElements = document.querySelectorAll('.payroll-map-fullscreen');
                        if (fsElements.length > 0 || document.fullscreenElement) {
                            if (document.fullscreenElement) {
                                try { document.exitFullscreen(); } catch { }
                            }
                            fsElements.forEach(function (node) {
                                node.classList.remove('payroll-map-fullscreen');
                            });
                            document.body.classList.remove('payroll-fullscreen-active');
                            exitAdminFullscreen();
                        }
                    }
                });

                window.__payrollAdminMapFullscreenBound = true;
            }
            const panel = document.createElement('div');
            panel.className = 'payroll-premium-map-ui';
            panel.innerHTML =
                '<div class="payroll-map-commandbar">' +
                '<div class="payroll-map-search-wrap">' +
                '<span class="payroll-map-search-icon">⌕</span>' +
                '<input class="payroll-map-search" type="search" placeholder="Search staff" autocomplete="off" aria-label="Search staff" />' +
                '</div>' +
                '<select class="payroll-map-filter" aria-label="Map filter">' +
                '<option value="all">All staff</option>' +
                '<option value="live">Live</option>' +
                '<option value="stale">Stale</option>' +
                '<option value="offline">Offline</option>' +
                '<option value="within">Within range</option>' +
                '<option value="outside">Outside range</option>' +
                '</select>' +
                '<button type="button" class="payroll-map-tool-toggle" data-map-action="toggle" title="Map tools" aria-label="Map tools"><i class="bi bi-tools"></i><span>Tools</span></button>' +
                '<div class="payroll-map-tools-group">' +
                '<button type="button" class="payroll-map-tool" data-map-action="fit" title="Fit all staff" aria-label="Fit all staff"><i class="bi bi-arrows-fullscreen"></i><span>Fit</span></button>' +
                '<button type="button" class="payroll-map-tool" data-map-action="office" title="Focus office" aria-label="Focus office"><i class="bi bi-building"></i><span>Office</span></button>' +
                '<button type="button" class="payroll-map-tool" data-map-action="follow" title="Follow selected staff" aria-label="Follow selected staff"><i class="bi bi-crosshair"></i><span>Follow</span></button>' +
                '<button type="button" class="payroll-map-tool" data-map-action="geofence" title="Toggle geofence" aria-label="Toggle geofence"><i class="bi bi-bullseye"></i><span>Zone</span></button>' +
                '<button type="button" class="payroll-map-tool" data-map-action="trails" title="Toggle journey trails" aria-label="Toggle journey trails"><i class="bi bi-bezier2"></i><span>Trails</span></button>' +
                '<button type="button" class="payroll-map-tool" data-map-action="layer" title="Change map layer" aria-label="Change map layer"><i class="bi bi-layers"></i><span>Layers</span></button>' +
                '<button type="button" class="payroll-map-tool" data-map-action="fullscreen" title="Full screen map" aria-label="Full screen map"><i class="bi bi-fullscreen"></i><span>Full</span></button>' +
                '</div>' +
                '</div>' +
                '<div class="payroll-map-statusbar">' +
                '<span class="payroll-map-status-live"><i></i><strong data-map-live>0</strong> live</span>' +
                '<span class="payroll-map-status-stale"><i></i><strong data-map-stale>0</strong> stale</span>' +
                '<span class="payroll-map-status-out"><i></i><strong data-map-out>0</strong> outside</span>' +
                '<span class="payroll-map-status-updated">● realtime</span>' +
                '</div>';
            const panelHost = container.closest('.admin-map-wrapper') || container.parentElement || container;
            panelHost.appendChild(panel);

            const search = panel.querySelector('.payroll-map-search');
            const filter = panel.querySelector('.payroll-map-filter');
            search.value = state.uiSearch;
            filter.value = state.uiFilter;

            search.addEventListener('input', function () {
                state.uiSearch = this.value.trim().toLowerCase();
                window.applyPremiumAdminMapFilter(mapId);
            });
            filter.addEventListener('change', function () {
                state.uiFilter = this.value;
                window.applyPremiumAdminMapFilter(mapId);
            });

            panel.querySelectorAll('[data-map-action]').forEach(function (button) {
                button.addEventListener('click', function () {
                    window.handlePremiumAdminMapAction(mapId, this.dataset.mapAction);
                });
            });

            state.premiumControls = panel;

            // Move Leaflet's native +/- controls into the same horizontal
            // toolbar that sits below the map. This keeps the map viewport
            // clean and prevents a second grey control strip.
            const zoomHost = document.getElementById(mapId + '-zoom-controls');
            const zoomContainer = map.zoomControl?.getContainer?.();
            const commandbar = panel.querySelector('.payroll-map-commandbar');
            const statusbar = panel.querySelector('.payroll-map-statusbar');
            if (statusbar && commandbar) {
                commandbar.appendChild(statusbar);
            }
            if (zoomContainer && commandbar) {
                zoomContainer.classList.add('payroll-bottom-zoom-control');
                commandbar.appendChild(zoomContainer);
            } else if (zoomHost && commandbar) {
                commandbar.appendChild(zoomHost);
            }
        }

        // Base layers are created once and selected without replacing the map.
        if (!state.baseLayers) {
            state.baseLayers = {
                standard: L.tileLayer(
                    'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
                    { maxZoom: 19, attribution: '© OpenStreetMap contributors' }
                ),
                dark: L.tileLayer(
                    'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
                    {
                        maxZoom: 19,
                        className: 'payroll-dark-osm-tiles',
                        attribution: '© OpenStreetMap contributors'
                    }
                ),
                satellite: L.tileLayer(
                    'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
                    { maxZoom: 19, attribution: 'Tiles © Esri' }
                )
            };
        }

        // Respect the application's current theme on first creation.
        if (!state.baseLayerInitialized) {
            state.baseLayer = (document.body.classList.contains('dark') || document.body.getAttribute('data-theme') === 'dark' || document.body.getAttribute('data-bs-theme') === 'dark') ? 'dark' : 'standard';
            state.baseLayerInitialized = true;
        }
        window.setPremiumAdminMapLayer(mapId, state.baseLayer);

        // Premium map scale control is native Leaflet and presentation-only.
        if (!state.scaleControl) {
            state.scaleControl = L.control.scale({ imperial: false, position: 'bottomright', maxWidth: 120 }).addTo(map);
        }

        if (!state.themeObserver) {
            state.themeObserver = new MutationObserver(function () {
                if (!state.userSelectedLayer) {
                    window.setPremiumAdminMapLayer(mapId, (document.body.classList.contains('dark') || document.body.getAttribute('data-theme') === 'dark' || document.body.getAttribute('data-bs-theme') === 'dark') ? 'dark' : 'standard');
                }
            });
            state.themeObserver.observe(document.body, { attributes: true, attributeFilter: ['class', 'data-theme', 'data-bs-theme'] });
        }

        window.applyPremiumAdminMapFilter(mapId);
        window.ensureAdminSelectedEmployeeRail?.(mapId, state.selectedId);
    } catch (error) {
        console.debug('Premium admin map controls deferred:', error);
    }
};

window.setPremiumAdminMapLayer = function (mapId, layerName) {
    const state = window.adminLiveMaps?.[mapId];
    if (!state?.map) return;
    const name = layerName === 'dark' || layerName === 'satellite' ? layerName : 'standard';
    try {
        Object.keys(state.baseLayers || {}).forEach(function (key) {
            const layer = state.baseLayers[key];
            if (!layer) return;
            if (key === name) layer.addTo(state.map);
            else if (state.map.hasLayer(layer)) state.map.removeLayer(layer);
        });
        state.baseLayer = name;
        if (name !== 'standard') state.userSelectedLayer = true;
        const btn = state.premiumControls?.querySelector('[data-map-action="layer"]');
        if (btn) btn.innerHTML = (name === 'dark' ? '☾' : name === 'satellite' ? '▧' : '▦') + ' <span>' + (name === 'dark' ? 'Dark' : name === 'satellite' ? 'Satellite' : 'Map') + '</span>';
    } catch { }
};

window.applyPremiumAdminMapFilter = function (mapId) {
    const state = window.adminLiveMaps?.[mapId];
    if (!state?.map) return;
    const search = String(state.uiSearch || '').toLowerCase();
    const filter = state.uiFilter || 'all';
    const staff = Array.isArray(state.liveStaff) ? state.liveStaff : [];
    let live = 0, stale = 0, outside = 0;

    const selectedId = Number(state.lastSelectedId || 0);
    const isPlayback = !!state.isPlayback;

    staff.forEach(function (x) {
        const id = Number(x.employeeId);
        const status = String(x.status || 'live').toLowerCase();
        const within = Boolean(x.isWithinAllowedRadius);
        if (status === 'live') live++;
        if (status === 'stale') stale++;
        if (!within) outside++;

        const isSelected = selectedId === id;
        const name = String(x.name || ('Employee ' + id)).toLowerCase();

        const matchesPlayback = !isPlayback;

        const matchesSearch = !search || name.includes(search) || String(id).includes(search);
        const matchesFilter =
            filter === 'all' ||
            filter === status ||
            (filter === 'within' && within) ||
            (filter === 'outside' && !within);

        const visible = matchesSearch && matchesFilter && matchesPlayback;

        const marker = state.markers?.[id];
        if (marker) {
            marker.setOpacity(visible ? (selectedId > 0 && !isSelected ? 0.8 : 1) : 0);
            if (visible) marker.setZIndexOffset(isSelected ? 3000 : 0);
            try {
                if (!visible && marker.isTooltipOpen()) marker.closeTooltip();
            } catch { }
        }
        ['roadRouteLines', 'roadRouteCasings', 'trails', 'collisionConnectors', 'labels', 'journeyLabels'].forEach(function (group) {
            const layer = state[group]?.[id];
            if (!layer) return;
            try {
                if (group === 'trails' && !state.trailsVisible) layer.setStyle({ opacity: 0 });
                else layer.setStyle({ opacity: visible ? (group === 'roadRouteLines' ? .82 : .55) : 0 });
            } catch { }
        });
    });

    const panel = state.premiumControls;
    if (panel) {
        panel.querySelector('[data-map-live]').textContent = String(live);
        panel.querySelector('[data-map-stale]').textContent = String(stale);
        panel.querySelector('[data-map-out]').textContent = String(outside);
        panel.querySelector('[data-map-action="follow"]').classList.toggle('active', !!state.followSelected);
        panel.querySelector('[data-map-action="geofence"]').classList.toggle('active', state.geofenceVisible);
        panel.querySelector('[data-map-action="trails"]').classList.toggle('active', state.trailsVisible);
    }
};

window.syncAdminSelectedRailFullscreen = function (state, rail, selectedId) {
    try {
        const mapContainer = state?.map?.getContainer?.();
        const fullscreenHost = mapContainer?.closest('.admin-map-wrapper') || mapContainer;
        if (!mapContainer || !rail || Number(selectedId || 0) <= 0 || document.fullscreenElement !== fullscreenHost) {
            if (state?.fullscreenSelectedRail?.parentElement) state.fullscreenSelectedRail.remove();
            if (state) state.fullscreenSelectedRail = null;
            return;
        }

        let clone = state.fullscreenSelectedRail;
        if (!clone || !clone.isConnected) {
            clone = rail.cloneNode(true);
            clone.classList.add('admin-selected-employee-rail-fullscreen');
            clone.removeAttribute('data-admin-selected-rail');
            clone.setAttribute('aria-label', 'Selected employee location details fullscreen');
            fullscreenHost.appendChild(clone);
            state.fullscreenSelectedRail = clone;

            const pause = () => { state.fullscreenSelectedRailPaused = true; };
            const resume = () => { state.fullscreenSelectedRailPaused = false; };
            clone.addEventListener('mouseenter', pause);
            clone.addEventListener('mouseleave', resume);
            clone.addEventListener('focusin', pause);
            clone.addEventListener('focusout', resume);
            clone.addEventListener('touchstart', pause, { passive: true });
            clone.addEventListener('touchend', () => {
                window.clearTimeout(state.fullscreenRailResumeTimer);
                state.fullscreenRailResumeTimer = window.setTimeout(resume, 650);
            }, { passive: true });
            clone.addEventListener('wheel', () => {
                pause();
                window.clearTimeout(state.fullscreenRailResumeTimer);
                state.fullscreenRailResumeTimer = window.setTimeout(resume, 650);
            }, { passive: true });
        }

        clone.innerHTML = rail.innerHTML;
        const track = clone.querySelector('.admin-selected-employee-rail-track');
        const firstSet = track?.children?.[0];
        const maxLoop = firstSet ? firstSet.offsetWidth : 0;
        const needsAutoScroll = !!(track && maxLoop > 0 && clone.scrollWidth > clone.clientWidth + 4);
        clone.classList.toggle('is-auto-scrolling', needsAutoScroll);
        clone.classList.toggle('is-auto-scroll-paused', !!state.fullscreenSelectedRailPaused);
    } catch { }
};

window.ensureAdminSelectedEmployeeRail = function (mapId, selectedId) {
    try {
        const state = window.adminLiveMaps?.[mapId];
        if (!state?.map) return;
        const mapContainer = state.map.getContainer();
        const wrapper = mapContainer.closest('.admin-map-wrapper') || mapContainer.parentElement;
        const rail = document.querySelector('[data-admin-selected-rail="' + CSS.escape(String(mapId)) + '"]');

        if (!rail || Number(selectedId || 0) <= 0) {
            if (state.selectedRailFrame) cancelAnimationFrame(state.selectedRailFrame);
            state.selectedRailFrame = null;
            state.selectedRailElement = null;
            return;
        }

        if (state.selectedRailElement !== rail) {
            state.selectedRailElement = rail;
            state.selectedRailPaused = false;
            state.selectedRailAddressKey = '';
            state.selectedRailAddressCache = state.selectedRailAddressCache || {};

            const pause = () => { state.selectedRailPaused = true; rail.classList.add('is-auto-scroll-paused'); };
            const resume = () => { state.selectedRailPaused = false; rail.classList.remove('is-auto-scroll-paused'); };
            rail.addEventListener('mouseenter', pause);
            rail.addEventListener('mouseleave', resume);
            rail.addEventListener('focusin', pause);
            rail.addEventListener('focusout', resume);
            rail.addEventListener('touchstart', pause, { passive: true });
            rail.addEventListener('touchend', () => {
                window.clearTimeout(state.selectedRailResumeTimer);
                state.selectedRailResumeTimer = window.setTimeout(resume, 650);
            }, { passive: true });
            rail.addEventListener('wheel', () => {
                pause();
                window.clearTimeout(state.selectedRailResumeTimer);
                state.selectedRailResumeTimer = window.setTimeout(resume, 650);
            }, { passive: true });
        }

        const track = rail.querySelector('.admin-selected-employee-rail-track');
        const firstSet = track?.children?.[0];
        const maxLoop = firstSet ? firstSet.offsetWidth : 0;
        const needsAutoScroll = !!(track && maxLoop > 0 && rail.scrollWidth > rail.clientWidth + 4);
        rail.classList.toggle('is-auto-scrolling', needsAutoScroll);
        rail.classList.toggle('is-auto-scroll-paused', !!state.selectedRailPaused);

        const selected = (state.liveStaff || []).find(x => Number(x.employeeId) === Number(selectedId));
        if (selected) {
            const employeeId = Number(selected.employeeId);
            const lat = Number(selected.latitude);
            const lon = Number(selected.longitude);
            const key = employeeId + ':' + lat.toFixed(6) + ':' + lon.toFixed(6);
            const addresses = Array.from(rail.querySelectorAll('[data-selected-address]'));
            const detailAddress = document.querySelector('[data-selected-detail-address]');
            const cache = state.selectedRailAddressCache || (state.selectedRailAddressCache = {});
            const cached = String(cache[employeeId] || '');

            // Always keep the last successful address visible while the newest GPS
            // point is being reverse-geocoded. A transient geocoder failure must
            // never replace a known-good address with "Address unavailable".
            const paintAddress = function (text, coordsText) {
                addresses.forEach(function (address) {
                    const strong = address.querySelector('strong');
                    const coords = address.querySelector('[data-selected-coords]');
                    if (strong && text) strong.textContent = text;
                    if (coords) coords.textContent = coordsText || (lat.toFixed(6) + ' · ' + lon.toFixed(6));
                });
                if (detailAddress && text) detailAddress.textContent = text;
            };

            if (cached) {
                paintAddress(cached, lat.toFixed(6) + ' · ' + lon.toFixed(6));
            }

            // Keep live telemetry visible even if the selected rail is in a long
            // marquee and Blazor has just replaced its DOM nodes.
            const speedNodes = Array.from(rail.querySelectorAll('[data-selected-speed]'));
            const radiusNodes = Array.from(rail.querySelectorAll('[data-selected-radius]'));
            const rangeNodes = Array.from(rail.querySelectorAll('[data-selected-range]'));
            const speed = Number(selected.speedMps ?? selected.SpeedMps) || 0;
            const radius = Number(selected.allowedRadiusMeters ?? selected.AllowedRadiusMeters) || 0;
            const distance = Number(selected.distanceMeters ?? selected.DistanceMeters) || 0;
            const within = Boolean(selected.isWithinAllowedRadius ?? selected.IsWithinAllowedRadius);
            const movement = String(selected.movementState ?? selected.MovementState ?? 'Stopped');
            const speedText = (!Number.isFinite(speed) || speed <= 0.15) ? 'Stopped' : ((speed * 3.6) < 1 ? 'Slow' : (speed * 3.6).toFixed(1) + ' km/h');
            speedNodes.forEach(node => {
                const strong = node.querySelector('strong');
                const small = node.querySelector('small');
                if (strong) strong.textContent = speedText;
                if (small) small.textContent = movement;
            });
            radiusNodes.forEach(node => {
                const strong = node.querySelector('strong');
                if (strong) strong.textContent = radius + ' m';
            });
            rangeNodes.forEach(node => {
                const strong = node.querySelector('strong');
                const small = node.querySelector('small');
                if (strong) {
                    strong.textContent = within ? 'Inside range' : 'Outside range';
                    strong.classList.toggle('text-success', within);
                    strong.classList.toggle('text-danger', !within);
                }
                if (small) small.textContent = (distance < 1000 ? Math.round(distance) + ' m' : (distance / 1000).toFixed(1) + ' km') + ' from shop';
            });

            if (addresses.length && key !== state.selectedRailAddressKey && Number.isFinite(lat) && Number.isFinite(lon)) {
                state.selectedRailAddressKey = key;
                if (!cached) paintAddress('Resolving current address...', lat.toFixed(6) + ' · ' + lon.toFixed(6));
                const applyAddress = function (text, displayName) {
                    if (state.selectedRailElement !== rail || state.selectedRailAddressKey !== key) return;
                    const finalText = text || displayName || '';
                    if (!finalText) {
                        // Preserve the last known address. If none exists, show GPS
                        // coordinates rather than the misleading "Address unavailable".
                        if (!cache[employeeId]) paintAddress('GPS ' + lat.toFixed(6) + ', ' + lon.toFixed(6), lat.toFixed(6) + ' · ' + lon.toFixed(6));
                        return;
                    }
                    cache[employeeId] = finalText;
                    paintAddress(finalText, displayName || (lat.toFixed(6) + ' · ' + lon.toFixed(6)));
                };

                if (typeof window.payrollReverseGeocodeLocation === 'function') {
                    window.payrollReverseGeocodeLocation(lat, lon).then(function (addr) {
                        applyAddress(addr, addr);
                    }).catch(function () {
                        applyAddress('', '');
                    });
                } else {
                    applyAddress('', '');
                }
            }
        }

        window.syncAdminSelectedRailFullscreen?.(state, rail, selectedId);
    } catch { }
};

window.focusAdminLiveEmployee = function (mapId, employeeId) {
    const state = window.adminLiveMaps?.[mapId];
    if (!state?.map) return;
    const id = Number(employeeId);
    const marker = state.markers?.[id];
    if (marker) {
        const points = [];
        if (state.office && Array.isArray(state.office) && Number.isFinite(state.office[0])) {
            points.push(state.office);
        }
        points.push(marker.getLatLng());
        if (points.length > 1) {
            const bounds = L.latLngBounds(points);
            if (bounds.isValid()) {
                state.map.fitBounds(bounds, { padding: [60, 60], maxZoom: 16, animate: true });
            }
        } else {
            state.map.setView(marker.getLatLng(), 15, { animate: true });
        }
        try { marker.openTooltip(); } catch { }
    }
};

window.payrollUpdateAdminMapFullscreenUI = function (mapId, isFullscreen) {
    try {
        const icons = document.querySelectorAll(`[data-fullscreen-icon="${CSS.escape(String(mapId))}"]`);
        icons.forEach(function (icon) {
            icon.className = isFullscreen ? 'bi bi-fullscreen-exit' : 'bi bi-fullscreen';
        });
        const state = window.adminLiveMaps?.[mapId];
        const btn = state?.premiumControls?.querySelector('[data-map-action="fullscreen"]');
        if (btn) {
            btn.innerHTML = (isFullscreen ? '<i class="bi bi-fullscreen-exit"></i><span>Exit</span>' : '<i class="bi bi-fullscreen"></i><span>Full</span>');
        }
    } catch { }
};

window.handlePremiumAdminMapAction = function (mapId, action) {
    const state = window.adminLiveMaps?.[mapId];
    if (!state?.map) return;

    if (action === 'toggle') {
        const bar = state.premiumControls?.querySelector('.payroll-map-commandbar');
        const btn = state.premiumControls?.querySelector('.payroll-map-tool-toggle');
        if (bar && btn) {
            const expanded = bar.classList.toggle('expanded');
            btn.classList.toggle('active', expanded);
            btn.innerHTML = expanded ? '⚙ <span>Hide</span>' : '⚙ <span>Tools</span>';
        }
        return;
    }

    const points = [state.office].filter(Boolean);
    (state.liveStaff || []).forEach(function (x) {
        const lat = Number(x.latitude), lng = Number(x.longitude);
        if (Number.isFinite(lat) && Number.isFinite(lng)) points.push([lat, lng]);
    });

    if (action === 'fit') {
        if (state.markerDotNetRef && Number(state.selectedId) > 0) {
            try { state.markerDotNetRef.invokeMethodAsync('SelectEmployeeFromMap', 0); } catch (e) { }
        }
        const bounds = L.latLngBounds(points);
        if (bounds.isValid()) state.map.fitBounds(bounds, { padding: [70, 70], maxZoom: 17, animate: true, duration: .8 });
    } else if (action === 'zoom-in' || action === 'zoomin') {
        state.map.zoomIn();
    } else if (action === 'zoom-out' || action === 'zoomout') {
        state.map.zoomOut();
    } else if (action === 'recenter') {
        if (state.selectedId > 0 && state.markers?.[state.selectedId]) {
            const selPoints = [state.office, state.markers[state.selectedId].getLatLng()].filter(Boolean);
            if (selPoints.length > 1) {
                const b = L.latLngBounds(selPoints);
                if (b.isValid()) state.map.fitBounds(b, { padding: [60, 60], maxZoom: 17, animate: true });
            } else {
                state.map.setView(state.markers[state.selectedId].getLatLng(), Math.max(16, state.map.getZoom()), { animate: true });
            }
        } else {
            const bounds = L.latLngBounds(points);
            if (bounds.isValid()) state.map.fitBounds(bounds, { padding: [70, 70], maxZoom: 17, animate: true });
            else if (state.office) state.map.setView(state.office, 15, { animate: true });
        }
    } else if (action === 'office') {
        state.map.setView(state.office, Math.max(15, state.map.getZoom()), { animate: true });
    } else if (action === 'follow') {
        state.followSelected = !state.followSelected;
        if (state.followSelected && state.selectedId > 0 && state.markers?.[state.selectedId]) {
            state.map.panTo(state.markers[state.selectedId].getLatLng(), { animate: true, duration: .6 });
        }
    } else if (action === 'geofence') {
        state.geofenceVisible = !state.geofenceVisible;
        if (state.circle) state.circle.setStyle({ opacity: state.geofenceVisible ? .72 : 0, fillOpacity: state.geofenceVisible ? .08 : 0 });
    } else if (action === 'trails') {
        state.trailsVisible = !state.trailsVisible;
        Object.values(state.trails || {}).forEach(function (layer) { try { layer.setStyle({ opacity: state.trailsVisible ? .9 : 0 }); } catch { } });
    } else if (action === 'layer') {
        const next = state.baseLayer === 'standard' ? 'dark' : state.baseLayer === 'dark' ? 'satellite' : 'standard';
        state.userSelectedLayer = next !== 'standard';
        window.setPremiumAdminMapLayer(mapId, next);
    } else if (action === 'fullscreen') {
        const el = state.map.getContainer();
        const fullscreenHost = el.closest('.admin-map-wrapper') || el;
        const bar = state.premiumControls?.querySelector('.payroll-map-commandbar');
        const isCurrentlyFs = !!(document.fullscreenElement || fullscreenHost.classList.contains('payroll-map-fullscreen') || el.classList.contains('payroll-map-fullscreen'));

        if (!isCurrentlyFs) {
            fullscreenHost.classList.add('payroll-map-fullscreen');
            el.classList.add('payroll-map-fullscreen');
            document.body.classList.add('payroll-fullscreen-active');
            if (fullscreenHost.requestFullscreen) {
                fullscreenHost.requestFullscreen().catch(function () { });
            }
            if (bar) bar.classList.remove('is-fullscreen-hidden');
            const selectedRail = state.selectedRailElement || document.querySelector('[data-admin-selected-rail="' + CSS.escape(String(mapId)) + '"]');
            if (selectedRail) window.syncAdminSelectedRailFullscreen?.(state, selectedRail, state.selectedId);
            window.payrollUpdateAdminMapFullscreenUI?.(mapId, true);
        } else {
            if (document.fullscreenElement) {
                try { document.exitFullscreen(); } catch { }
            }
            fullscreenHost.classList.remove('payroll-map-fullscreen');
            el.classList.remove('payroll-map-fullscreen');
            document.body.classList.remove('payroll-fullscreen-active');
            if (bar) bar.classList.remove('is-fullscreen-hidden');
            if (state.fullscreenSelectedRail?.parentElement) state.fullscreenSelectedRail.remove();
            state.fullscreenSelectedRail = null;
            window.payrollUpdateAdminMapFullscreenUI?.(mapId, false);
        }
        setTimeout(function () { try { state.map.invalidateSize({ animate: false }); } catch { } }, 50);
        setTimeout(function () { try { state.map.invalidateSize({ animate: false }); } catch { } }, 200);
        setTimeout(function () { try { state.map.invalidateSize({ animate: false }); } catch { } }, 450);
    }
    window.applyPremiumAdminMapFilter(mapId);
};

// ============================================================
// PREMIUM EMPLOYEE GEO MAP CONTROLS
// ============================================================
window.enhanceEmployeeGeoMap = function (mapId) {
    try {
        const state = window.payrollGeoMaps?.[mapId];
        if (!state?.map) return;
        const map = state.map;
        const container = map.getContainer();
        if (!state.premiumControls) {
            const panel = document.createElement('div');
            panel.className = 'payroll-premium-employee-map-ui';
            panel.innerHTML =
                '<div class="payroll-employee-map-tools">' +
                '<button type="button" data-geo-action="zoomin" title="Zoom in">+</button>' +
                '<button type="button" data-geo-action="zoomout" title="Zoom out">−</button>' +
                '<button type="button" data-geo-action="recenter" title="Center my location">📍 <span>Me</span></button>' +
                '<button type="button" data-geo-action="route" title="Fit office and your location">⌖ <span>Route</span></button>' +
                '<button type="button" data-geo-action="office" title="Focus office">🏢 <span>Office</span></button>' +
                '<button type="button" data-geo-action="layer" title="Change map layer">▦ <span>Layers</span></button>' +
                '</div>' +
                '<div class="payroll-employee-map-live">' +
                '<span class="payroll-map-live-dot"></span><strong>GPS LIVE</strong>' +
                '<span class="payroll-employee-map-accuracy"></span>' +
                '</div>';
            container.appendChild(panel);
            panel.querySelectorAll('[data-geo-action]').forEach(function (button) {
                button.addEventListener('click', function () {
                    const action = this.dataset.geoAction;
                    if (action === 'zoomin') {
                        map.zoomIn();
                    } else if (action === 'zoomout') {
                        map.zoomOut();
                    } else if (action === 'recenter') {
                        if (state.userMarker) {
                            map.setView(state.userMarker.getLatLng(), Math.max(16, map.getZoom()), { animate: true });
                        }
                    } else if (action === 'route') {
                        state.showRouteToOffice = true;
                        const b = L.latLngBounds([state.office, state.userMarker.getLatLng()]);
                        if (b.isValid()) map.fitBounds(b, { padding: [70, 70], maxZoom: 17, animate: true, duration: .7 });

                        // Force immediate route visibility update
                        state.roadRouteCasing?.setStyle({ opacity: .78 });
                        state.roadRouteLine?.setStyle({ opacity: .98 });
                        state.routeLine?.setLatLngs([]); state.routeLine?.setStyle({ opacity: 0 });
                    } else if (action === 'office') {
                        state.showRouteToOffice = true;
                        map.setView(state.office, Math.max(15, map.getZoom()), { animate: true });

                        // Force immediate route visibility update
                        state.roadRouteCasing?.setStyle({ opacity: .78 });
                        state.roadRouteLine?.setStyle({ opacity: .98 });
                        state.routeLine?.setLatLngs([]); state.routeLine?.setStyle({ opacity: 0 });
                    } else if (action === 'layer') {
                        state.baseLayer = state.baseLayer === 'standard' ? 'dark' : state.baseLayer === 'dark' ? 'satellite' : 'standard';
                        if (!state.baseLayers) state.baseLayers = {};
                        if (!state.baseLayers.dark) state.baseLayers.dark = L.tileLayer(
                            'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',
                            {
                                maxZoom: 19,
                                className: 'payroll-dark-osm-tiles',
                                attribution: '© OpenStreetMap contributors'
                            }
                        );
                        if (!state.baseLayers.satellite) state.baseLayers.satellite = L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', { maxZoom: 19, attribution: 'Tiles © Esri' });
                        Object.keys(state.baseLayers).forEach(function (k) { const l = state.baseLayers[k]; if (!l) return; if (k === state.baseLayer) l.addTo(map); else if (map.hasLayer(l)) map.removeLayer(l); });
                    }
                });
            });
            state.premiumControls = panel;
        }
        if (!state.scaleControl) state.scaleControl = L.control.scale({ imperial: false, position: 'bottomright', maxWidth: 120 }).addTo(map);

        // The Remote Punch card can change width at responsive breakpoints.
        // Keep Leaflet sized to the actual map box so its controls remain
        // positioned correctly after the layout changes.
        if (!state._layoutObserver && typeof ResizeObserver !== 'undefined') {
            state._layoutObserver = new ResizeObserver(function () {
                try { map.invalidateSize({ pan: false, animate: false }); } catch (_) { }
            });
            state._layoutObserver.observe(container);
        }
        requestAnimationFrame(function () {
            try { map.invalidateSize({ pan: false, animate: false }); } catch (_) { }
        });

        const acc = state.premiumControls.querySelector('.payroll-employee-map-accuracy');
        if (acc) acc.textContent = state.lastAccuracyMeters > 0 ? '±' + Math.round(state.lastAccuracyMeters) + ' m' : '';
    } catch (error) { console.debug('Premium employee map controls deferred:', error); }
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

            const initialSmooth = window.payrollGenerateSmoothSpline(route);

            state.historyRouteCasing = L.polyline(
                initialSmooth,
                {
                    color: '#ffffff',
                    weight: 8,
                    opacity: 0.85,
                    lineJoin: 'round',
                    lineCap: 'round'
                }
            ).addTo(state.map);

            state.historyRoute = L.polyline(
                initialSmooth,
                {
                    color: '#10b981',
                    weight: 5,
                    opacity: 0.95,
                    lineJoin: 'round',
                    lineCap: 'round'
                }
            ).addTo(state.map);

            window.payrollFetchMultiPointRoadRoute(route).then(function (res) {
                if (res?.geometry && state.historyRoute && state.historyRouteCasing) {
                    state.historyRouteCasing.setLatLngs(res.geometry);
                    state.historyRoute.setLatLngs(res.geometry);
                }
            }).catch(function () { });

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

        try {
            if (state.historyRouteCasing) {
                state.map.removeLayer(
                    state.historyRouteCasing
                );
            }
        }
        catch { }

        state.historyRoute = null;
        state.historyRouteCasing = null;
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

        if (state?.stayHubMarkers) {
            Object.values(state.stayHubMarkers).forEach(function (m) {
                try { state.map.removeLayer(m); } catch (_) { }
            });
            state.stayHubMarkers = {};
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

        try { if (state?._layoutObserver) state._layoutObserver.disconnect(); } catch (_) { }
        try { if (state?.liveSummaryTimer) clearInterval(state.liveSummaryTimer); } catch (_) { }
        state.liveSummaryTimer = null;

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

                    // Route playback follows the original device capture time.
                    // Fall back to server record time only for legacy rows.
                    const recordedRaw =
                        x.capturedAtUtc ??
                        x.CapturedAtUtc ??
                        x.recordedAtUtc ??
                        x.RecordedAtUtc;
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

            playback.travelledCasing = L.polyline(
                points.slice(0, initialIndex + 1).map(function (point) {
                    return [point.latitude, point.longitude];
                }),
                {
                    color: "#ffffff",
                    weight: 9,
                    opacity: 0.85,
                    lineJoin: "round",
                    lineCap: "round"
                }
            ).addTo(state.map);

            playback.travelledLine = L.polyline(
                points.slice(0, initialIndex + 1).map(function (point) {
                    return [point.latitude, point.longitude];
                }),
                {
                    color: "#10b981",
                    weight: 5.5,
                    opacity: 0.98,
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
                    opacity: 0.6,
                    dashArray: "6,6",
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
                    if (live.travelledCasing) {
                        live.travelledCasing.setLatLngs(travelled);
                    }
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
                    playback.travelledCasing,
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

// ============================================================================
// LOCATION TRACKING HISTORY / >10 MINUTE STAY MAP
// Presentation-only map for the dedicated Admin location history screen.
// It reads derived stay records from the page and never changes GPS data.
// ============================================================================
window.locationStayHistoryMaps = window.locationStayHistoryMaps || {};

window.initializeLocationStayHistoryMap = async function (mapId, payload) {
    try {
        await window.loadPayrollLeaflet();
        const element = document.getElementById(mapId);
        if (!element || !window.L) return;

        const existing = window.locationStayHistoryMaps[mapId];
        if (existing?.map) {
            try { existing.map.remove(); } catch { }
            delete window.locationStayHistoryMaps[mapId];
        }

        const map = L.map(element, { zoomControl: true, attributionControl: true });
        const tiles = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '© OpenStreetMap contributors'
        }).addTo(map);

        const points = Array.isArray(payload?.points) ? payload.points : [];
        const stays = Array.isArray(payload?.stays) ? payload.stays : [];
        const bounds = [];
        const state = { map, stayMarkers: {}, stayData: stays };
        window.locationStayHistoryMaps[mapId] = state;

        const path = points
            .map(p => [Number(p.latitude ?? p.Latitude), Number(p.longitude ?? p.Longitude)])
            .filter(p => Number.isFinite(p[0]) && Number.isFinite(p[1]));

        if (path.length > 1) {
            const initialSmooth = window.payrollGenerateSmoothSpline(path);
            const pathCasing = L.polyline(initialSmooth, {
                color: '#ffffff',
                weight: 7,
                opacity: .85,
                lineCap: 'round',
                lineJoin: 'round'
            }).addTo(map);

            const pathLine = L.polyline(initialSmooth, {
                color: '#10b981',
                weight: 4.5,
                opacity: .95,
                lineCap: 'round',
                lineJoin: 'round'
            }).addTo(map);

            path.forEach(p => bounds.push(p));

            window.payrollFetchMultiPointRoadRoute(path).then(function (res) {
                if (res?.geometry && pathLine && pathCasing) {
                    pathCasing.setLatLngs(res.geometry);
                    pathLine.setLatLngs(res.geometry);
                }
            }).catch(function () { });
        }

        stays.forEach((stay, index) => {
            const lat = Number(stay.lat), lng = Number(stay.lng);
            if (!Number.isFinite(lat) || !Number.isFinite(lng)) return;

            const icon = L.divIcon({
                className: 'location-stay-marker-wrap',
                html: '<div class="location-stay-marker"><span>' + (index + 1) + '</span></div>',
                iconSize: [38, 38],
                iconAnchor: [19, 19]
            });

            const marker = L.marker([lat, lng], { icon }).addTo(map);
            marker.bindPopup(
                '<div class="stay-map-popup">' +
                '<strong>' + escapeStayMapHtml(stay.employee || 'Employee') + '</strong>' +
                '<div>' + escapeStayMapHtml(stay.location || 'Unknown / Other Location') + '</div>' +
                '<div class="popup-meta">' +
                '<span>Start<br><b>' + formatStayMapTime(stay.start) + '</b></span>' +
                '<span>End<br><b>' + formatStayMapTime(stay.end) + '</b></span>' +
                '<span>Duration<br><b>' + Number(stay.durationMinutes || 0).toFixed(0) + ' min</b></span>' +
                '<span>Accuracy<br><b>' + Number(stay.accuracy || 0).toFixed(1) + ' m</b></span>' +
                '</div>' +
                '<div class="small mt-2">' + lat.toFixed(6) + ', ' + lng.toFixed(6) + '</div>' +
                '</div>'
            );
            marker.on('click', function () {
                window.highlightLocationStayReport(mapId, Number(stay.id));
            });
            state.stayMarkers[Number(stay.id)] = marker;
            bounds.push([lat, lng]);
        });

        if (bounds.length) {
            map.fitBounds(bounds, { padding: [28, 28], maxZoom: 17 });
        } else {
            map.setView([20.5937, 78.9629], 5);
        }

        setTimeout(() => map.invalidateSize(), 120);
    } catch (error) {
        console.warn('Location stay history map initialization failed.', error);
    }
};

window.focusLocationStayHistory = async function (mapId, stayId) {
    const state = window.locationStayHistoryMaps?.[mapId];
    if (!state?.map) return;
    const marker = state.stayMarkers?.[Number(stayId)];
    if (marker) {
        state.map.setView(marker.getLatLng(), Math.max(state.map.getZoom(), 17), { animate: true });
        marker.openPopup();
    }
    window.highlightLocationStayReport(mapId, Number(stayId));
};

window.highlightLocationStayReport = function (mapId, stayId) {
    document.querySelectorAll('[id^="stay-row-"], [id^="stay-detail-row-"]').forEach(function (el) {
        el.classList.remove('selected');
    });
    const compact = document.getElementById('stay-row-' + stayId);
    const detail = document.getElementById('stay-detail-row-' + stayId);
    if (compact) {
        compact.classList.add('selected');
        compact.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
    if (detail) detail.classList.add('selected');
};

window.resolveLocationStayAddresses = async function (mapId, stays) {
    const state = window.locationStayHistoryMaps?.[mapId];
    const items = Array.isArray(stays) ? stays : [];
    for (const item of items) {
        const lat = Number(item.lat), lon = Number(item.lng);
        if (!Number.isFinite(lat) || !Number.isFinite(lon)) continue;
        const nodes = document.querySelectorAll('[data-stay-address="' + String(item.id) + '"]');
        try {
            const response = await fetch(
                'https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat=' + encodeURIComponent(lat) + '&lon=' + encodeURIComponent(lon) + '&zoom=18&addressdetails=1',
                { headers: { 'Accept': 'application/json' } }
            );
            const data = response.ok ? await response.json() : null;
            const address = data?.address || {};
            const parts = [
                address.road,
                address.neighbourhood || address.suburb,
                address.city || address.town || address.village,
                address.state_district,
                address.state,
                address.postcode,
                address.amenity || address.tourism || address.attraction
            ].filter(Boolean);
            const text = parts.length ? parts.join(', ') : 'Address unavailable';
            nodes.forEach(n => { n.textContent = text; });
        } catch {
            nodes.forEach(n => { n.textContent = 'Address unavailable'; });
        }
        await new Promise(resolve => setTimeout(resolve, 350));
    }
};

window.destroyLocationStayHistoryMap = function (mapId) {
    const state = window.locationStayHistoryMaps?.[mapId];
    if (state?.map) {
        try { state.map.remove(); } catch { }
    }
    if (window.locationStayHistoryMaps) delete window.locationStayHistoryMaps[mapId];
};

window.locationStaysPageMaps = window.locationStaysPageMaps || {};

window.renderLocationStaysMap = async function (mapId, stays, dotNetRef) {
    try {
        await window.loadPayrollLeaflet();
        const element = document.getElementById(mapId);
        if (!element || !window.L) return;

        const existing = window.locationStaysPageMaps[mapId];
        if (existing?.map) {
            try { existing.map.remove(); } catch { }
            delete window.locationStaysPageMaps[mapId];
        }

        const map = L.map(element, { zoomControl: true, attributionControl: true });
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '© OpenStreetMap contributors'
        }).addTo(map);

        const markers = {};
        const bounds = [];
        const items = Array.isArray(stays) ? stays : [];

        items.forEach((stay, index) => {
            const lat = Number(stay.latitude ?? stay.lat);
            const lng = Number(stay.longitude ?? stay.lng);
            if (!Number.isFinite(lat) || !Number.isFinite(lng)) return;

            const icon = L.divIcon({
                className: 'stay-marker',
                html: '<span>' + (index + 1) + '</span>',
                iconSize: [34, 34],
                iconAnchor: [17, 34],
                popupAnchor: [0, -34]
            });

            const marker = L.marker([lat, lng], { icon }).addTo(map);
            const popupContent =
                '<div style="min-width:200px">' +
                '<strong>' + escapeStayMapHtml(stay.name || 'Employee #' + stay.employeeId) + '</strong>' +
                '<div class="text-primary fw-bold small mt-1">Stay: ' + escapeStayMapHtml(stay.duration || '') + '</div>' +
                '<div class="small text-muted mt-1">' + lat.toFixed(6) + ', ' + lng.toFixed(6) + '</div>' +
                '<div id="stay-popup-addr-' + stay.id + '" class="small mt-1 text-secondary">📍 Resolving address...</div>' +
                '</div>';
            marker.bindPopup(popupContent);

            marker.on('click', function () {
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('SelectStayFromMap', Number(stay.id));
                }
                fetch('https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat=' + encodeURIComponent(lat) + '&lon=' + encodeURIComponent(lng))
                    .then(r => r.json())
                    .then(data => {
                        const addrEl = document.getElementById('stay-popup-addr-' + stay.id);
                        if (addrEl && data?.display_name) {
                            addrEl.textContent = '📍 ' + data.display_name;
                        }
                    }).catch(() => {});
            });

            markers[Number(stay.id)] = marker;
            bounds.push([lat, lng]);
        });

        if (bounds.length) {
            map.fitBounds(bounds, { padding: [35, 35], maxZoom: 16 });
        } else {
            map.setView([20.5937, 78.9629], 5);
        }

        window.locationStaysPageMaps[mapId] = { map, markers };
        setTimeout(() => map.invalidateSize(), 150);
    } catch (e) {
        console.warn('renderLocationStaysMap error:', e);
    }
};

window.focusLocationStay = function (mapId, stayId) {
    const state = window.locationStaysPageMaps?.[mapId];
    if (!state?.map) return;
    const marker = state.markers?.[Number(stayId)];
    if (marker) {
        state.map.setView(marker.getLatLng(), Math.max(state.map.getZoom(), 16), { animate: true });
        marker.openPopup();
    }
};

window.destroyLocationStaysMap = function (mapId) {
    const state = window.locationStaysPageMaps?.[mapId];
    if (state?.map) {
        try { state.map.remove(); } catch { }
    }
    if (window.locationStaysPageMaps) delete window.locationStaysPageMaps[mapId];
};

function formatStayMapTime(value) {
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return '-';
    return d.toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' });
}

function escapeStayMapHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, function (c) {
        return ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' })[c];
    });
}

(function ensureLocationStayHistoryStyles() {
    if (document.getElementById('location-stay-history-map-styles')) return;
    const style = document.createElement('style');
    style.id = 'location-stay-history-map-styles';
    style.textContent = `
        .location-stay-marker-wrap{background:transparent!important;border:0!important}
        .location-stay-marker{width:34px;height:34px;border-radius:50% 50% 50% 0;transform:rotate(-45deg);display:grid;place-items:center;background:#dc3545;border:3px solid #fff;box-shadow:0 5px 16px rgba(0,0,0,.32)}
        .location-stay-marker span{transform:rotate(45deg);color:#fff;font-size:11px;font-weight:900}
    `;
    document.head.appendChild(style);
})();

// ============================================================
// SUPERADMIN MULTI-TENANT WORKSPACE COOKIE SYNC
// ============================================================
window.setTenantCookie = function (tenantId) {
    try {
        var name = "BioMetric_SuperAdmin_ActiveTenant";
        var value = encodeURIComponent(tenantId || "biometricpayroll");
        var expires = "; max-age=" + (30 * 24 * 60 * 60) + "; path=/; SameSite=Lax";
        document.cookie = name + "=" + value + expires;
        try {
            localStorage.setItem("BioMetric_SuperAdmin_ActiveTenant", tenantId || "biometricpayroll");
        } catch (e) { }
    } catch (e) {
        console.warn("Unable to write tenant cookie", e);
    }
};

window.clearTenantCookie = function () {
    try {
        document.cookie = "BioMetric_SuperAdmin_ActiveTenant=; max-age=0; path=/; SameSite=Lax";
        try {
            localStorage.removeItem("BioMetric_SuperAdmin_ActiveTenant");
        } catch (e) { }
    } catch (e) { }
};

// ============================================================
// OFFICE GEOFENCE & LOCATION PICKER (LEAFLET + NOMINATIM)
// ============================================================
window.payrollGeofencePicker = {
    map: null,
    marker: null,
    circle: null,
    dotNetHelper: null,

    init: async function (containerId, initialLat, initialLng, radiusMeters, dotNetHelper) {
        this.dotNetHelper = dotNetHelper;
        await window.loadPayrollLeaflet();
        if (!window.L) return;

        const el = document.getElementById(containerId);
        if (!el) return;

        if (this.map) {
            try { this.map.remove(); } catch (e) { }
            this.map = null;
            this.marker = null;
            this.circle = null;
        }

        const validLat = Number.isFinite(initialLat) && initialLat !== 0;
        const validLng = Number.isFinite(initialLng) && initialLng !== 0;
        const lat = validLat ? initialLat : 12.9716;
        const lng = validLng ? initialLng : 80.2437;
        const rad = Number.isFinite(radiusMeters) && radiusMeters > 0 ? radiusMeters : 100;

        this.map = L.map(containerId, {
            center: [lat, lng],
            zoom: validLat && validLng ? 16 : 13,
            zoomControl: true
        });

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '&copy; OpenStreetMap contributors',
            maxZoom: 19
        }).addTo(this.map);

        const officeIcon = L.divIcon({
            className: 'office-geofence-picker-pin',
            html: '<div style="background-color: #dc3545; width: 34px; height: 34px; border-radius: 50% 50% 50% 0; transform: rotate(-45deg); display: flex; align-items: center; justify-content: center; box-shadow: 0 4px 12px rgba(0,0,0,0.35); border: 2px solid white; cursor: grab;"><span style="transform: rotate(45deg); font-size: 16px;">📍</span></div>',
            iconSize: [34, 34],
            iconAnchor: [17, 34],
            popupAnchor: [0, -34]
        });

        this.marker = L.marker([lat, lng], {
            draggable: true,
            icon: officeIcon
        }).addTo(this.map);

        this.circle = L.circle([lat, lng], {
            radius: rad,
            color: '#198754',
            fillColor: '#198754',
            fillOpacity: 0.18,
            weight: 2
        }).addTo(this.map);

        const self = this;

        this.marker.on('drag', function (e) {
            const pos = e.target.getLatLng();
            self.circle.setLatLng(pos);
        });

        this.marker.on('dragend', function (e) {
            const pos = e.target.getLatLng();
            self.circle.setLatLng(pos);
            self.reverseGeocode(pos.lat, pos.lng);
        });

        this.map.on('click', function (e) {
            const pos = e.latlng;
            self.marker.setLatLng(pos);
            self.circle.setLatLng(pos);
            self.reverseGeocode(pos.lat, pos.lng);
        });

        setTimeout(function () {
            if (self.map) {
                self.map.invalidateSize();
                self.map.setView([lat, lng], validLat && validLng ? 16 : 13);
            }
        }, 250);

        if (validLat && validLng) {
            this.reverseGeocode(lat, lng);
        }
    },

    setRadius: function (radiusMeters) {
        if (this.circle && Number.isFinite(radiusMeters) && radiusMeters > 0) {
            this.circle.setRadius(radiusMeters);
        }
    },

    useCurrentLocation: function () {
        const self = this;
        if (!navigator.geolocation) {
            alert("Geolocation is not supported by your browser.");
            return;
        }

        navigator.geolocation.getCurrentPosition(
            function (position) {
                const lat = position.coords.latitude;
                const lng = position.coords.longitude;
                if (self.map && self.marker && self.circle) {
                    self.marker.setLatLng([lat, lng]);
                    self.circle.setLatLng([lat, lng]);
                    self.map.setView([lat, lng], 17);
                    self.reverseGeocode(lat, lng);
                }
            },
            function (err) {
                console.warn("Geolocation failed", err);
                alert("Could not fetch current GPS location: " + (err.message || "Permission denied"));
            },
            { enableHighAccuracy: true, timeout: 10000, maximumAge: 0 }
        );
    },

    searchLocation: async function (query) {
        if (!query || !query.trim()) return;
        const self = this;
        try {
            const resp = await fetch('https://nominatim.openstreetmap.org/search?format=json&q=' + encodeURIComponent(query.trim()) + '&addressdetails=1&limit=1', {
                headers: { 'Accept': 'application/json' }
            });
            if (!resp.ok) return;
            const data = await resp.json();
            if (data && data.length > 0) {
                const first = data[0];
                const lat = parseFloat(first.lat);
                const lng = parseFloat(first.lon);
                if (self.map && self.marker && self.circle) {
                    self.marker.setLatLng([lat, lng]);
                    self.circle.setLatLng([lat, lng]);
                    self.map.setView([lat, lng], 17);
                    self.processAddressData(lat, lng, first);
                }
            } else {
                alert("Location not found. Try searching with a landmark, area or city name.");
            }
        } catch (e) {
            console.error("Location search failed", e);
        }
    },

    reverseGeocode: async function (lat, lng) {
        const self = this;
        try {
            const resp = await fetch('https://nominatim.openstreetmap.org/reverse?format=json&lat=' + lat + '&lon=' + lng + '&addressdetails=1', {
                headers: { 'Accept': 'application/json' }
            });
            if (!resp.ok) {
                if (self.dotNetHelper) {
                    self.dotNetHelper.invokeMethodAsync('OnLocationPinned', lat, lng, '', '');
                }
                return;
            }
            const data = await resp.json();
            self.processAddressData(lat, lng, data);
        } catch (e) {
            console.warn("Reverse geocode failed", e);
            if (self.dotNetHelper) {
                self.dotNetHelper.invokeMethodAsync('OnLocationPinned', lat, lng, '', '');
            }
        }
    },

    processAddressData: function (lat, lng, data) {
        if (!this.dotNetHelper) return;
        const a = (data && data.address) ? data.address : {};

        const lineParts = [];
        const premises = a.building || a.house_name || a.office || a.amenity || a.commercial || a.industrial || '';
        const houseNum = a.house_number || '';
        const road = a.road || a.pedestrian || a.street || a.residential || a.suburb || a.neighbourhood || '';

        if (premises) lineParts.push(premises);
        if (houseNum && road) {
            lineParts.push(houseNum + ' ' + road);
        } else if (road) {
            lineParts.push(road);
        } else if (houseNum) {
            lineParts.push(houseNum);
        }

        let addressLine1 = lineParts.join(', ');
        if (!addressLine1 && data.display_name) {
            const segs = data.display_name.split(',');
            addressLine1 = (segs.length > 0 ? segs[0].trim() : '');
        }

        const city = a.city || a.town || a.village || a.suburb || a.municipality || a.county || a.state_district || '';
        const state = a.state || a.region || '';
        const pincode = a.postcode || '';

        const cityStateParts = [];
        if (city) cityStateParts.push(city);
        if (state) cityStateParts.push(state);

        let cityStateStr = cityStateParts.join(', ');
        let cityStatePincode = cityStateStr;
        if (pincode) {
            cityStatePincode = cityStateStr ? (cityStateStr + ' - ' + pincode) : pincode;
        }

        this.dotNetHelper.invokeMethodAsync('OnLocationPinned', lat, lng, addressLine1, cityStatePincode);
    },

    destroy: function () {
        if (this.map) {
            try { this.map.remove(); } catch (e) { }
            this.map = null;
            this.marker = null;
            this.circle = null;
        }
        this.dotNetHelper = null;
    }
};

// ============================================================
// ULTRA-FAST NAVIGATION LOADER & PROGRESS BAR
// ============================================================
(function () {
    let progressTimer = null;
    let finishTimer = null;
    let currentProgress = 0;

    function getProgressBar() {
        return document.getElementById('payroll-top-progress-bar');
    }

    function getHeaderLoadingIndicator() {
        return document.getElementById('payroll-header-loading-indicator');
    }

    window.startNavigationProgress = function () {
        clearTimeout(finishTimer);
        clearInterval(progressTimer);

        const bar = getProgressBar();
        const headerPill = getHeaderLoadingIndicator();
        const content = document.querySelector('.page-transition-wrapper');

        try {
            const currentTitle = document.title ? document.title.replace(/^⏳\s*(Loading\.\.\.\s*\|\s*)?/, '') : 'BioMetric+Payroll';
            document.title = '⏳ Loading... | ' + currentTitle;
        } catch (_) { }

        if (headerPill) {
            headerPill.classList.remove('d-none');
            headerPill.classList.add('d-inline-flex');
        }

        if (content) {
            content.classList.add('is-navigating');
        }

        if (bar) {
            bar.classList.remove('finishing');
            bar.classList.add('active');
            currentProgress = 25;
            bar.style.width = currentProgress + '%';

            progressTimer = setInterval(function () {
                if (currentProgress < 85) {
                    currentProgress += Math.random() * 12 + 6;
                    if (currentProgress > 85) currentProgress = 85;
                    bar.style.width = currentProgress + '%';
                }
            }, 90);
        }
    };

    window.finishNavigationProgress = function () {
        clearInterval(progressTimer);

        const bar = getProgressBar();
        const headerPill = getHeaderLoadingIndicator();
        const content = document.querySelector('.page-transition-wrapper');

        if (content) {
            content.classList.remove('is-navigating');
        }

        if (headerPill) {
            headerPill.classList.remove('d-inline-flex');
            headerPill.classList.add('d-none');
        }

        if (bar) {
            currentProgress = 100;
            bar.style.width = '100%';
            bar.classList.add('finishing');

            finishTimer = setTimeout(function () {
                bar.classList.remove('active', 'finishing');
                bar.style.width = '0%';
                currentProgress = 0;
            }, 280);
        }
    };

    document.addEventListener('click', function (e) {
        const link = e.target.closest('a');
        if (!link) return;

        const href = link.getAttribute('href');
        const target = link.getAttribute('target');

        if (href && !href.startsWith('http') && !href.startsWith('javascript:') && !href.startsWith('#') && target !== '_blank') {
            link.classList.add('nav-clicking');
            setTimeout(function () { link.classList.remove('nav-clicking'); }, 300);
            window.startNavigationProgress();
        }
    }, true);
})();



