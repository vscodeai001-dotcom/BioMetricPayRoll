// Attendance read-model offline cache.
// This is a resilience cache only. Firebase remains the authoritative SSOT.
window.attendanceCache = (function () {
    const DB_NAME = 'biometricpayroll-attendance-cache';
    const STORE_NAME = 'attendance';
    const DB_VERSION = 1;
    let dbPromise = null;

    function openDb() {
        if (dbPromise) return dbPromise;
        if (!window.indexedDB) return Promise.reject(new Error('IndexedDB unavailable'));

        dbPromise = new Promise(function (resolve, reject) {
            const request = indexedDB.open(DB_NAME, DB_VERSION);
            request.onupgradeneeded = function () {
                const db = request.result;
                if (!db.objectStoreNames.contains(STORE_NAME)) {
                    db.createObjectStore(STORE_NAME, { keyPath: 'key' });
                }
            };
            request.onsuccess = function () { resolve(request.result); };
            request.onerror = function () { reject(request.error || new Error('IndexedDB open failed')); };
        });
        return dbPromise;
    }

    async function initialize() {
        try { await openDb(); } catch (_) { }
    }

    async function set(key, payload) {
        const db = await openDb();
        return new Promise(function (resolve, reject) {
            const tx = db.transaction(STORE_NAME, 'readwrite');
            tx.objectStore(STORE_NAME).put({ key: key, payload: payload, savedUtc: new Date().toISOString() });
            tx.oncomplete = function () { resolve(); };
            tx.onerror = function () { reject(tx.error || new Error('Attendance cache write failed')); };
        });
    }

    async function get(key) {
        const db = await openDb();
        return new Promise(function (resolve, reject) {
            const tx = db.transaction(STORE_NAME, 'readonly');
            const request = tx.objectStore(STORE_NAME).get(key);
            request.onsuccess = function () { resolve(request.result ? request.result.payload : null); };
            request.onerror = function () { reject(request.error || new Error('Attendance cache read failed')); };
        });
    }

    async function remove(key) {
        try {
            const db = await openDb();
            return new Promise(function (resolve, reject) {
                const tx = db.transaction(STORE_NAME, 'readwrite');
                tx.objectStore(STORE_NAME).delete(key);
                tx.oncomplete = function () { resolve(); };
                tx.onerror = function () { reject(tx.error || new Error('Attendance cache delete failed')); };
            });
        } catch (_) { }
    }

    async function clear() {
        try {
            const db = await openDb();
            return new Promise(function (resolve, reject) {
                const tx = db.transaction(STORE_NAME, 'readwrite');
                tx.objectStore(STORE_NAME).clear();
                tx.oncomplete = function () { resolve(); };
                tx.onerror = function () { reject(tx.error || new Error('Attendance cache clear failed')); };
            });
        } catch (_) { }
    }

    return { initialize: initialize, set: set, get: get, remove: remove, clear: clear };
})();
