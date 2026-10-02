// Employee read-model offline cache for the Web Employee Management screen.
// This is a browser read cache only. Firebase remains authoritative and no
// employee mutation is queued or replayed while offline.
window.employeeCache = (function () {
    const DB_NAME = 'biometricPayrollEmployeeCache';
    const DB_VERSION = 1;
    const STORE = 'readModels';
    const KEY = 'employee-roster-v1';
    const MAX_AGE_MS = 24 * 60 * 60 * 1000;

    function openDb() {
        return new Promise((resolve, reject) => {
            if (!window.indexedDB) {
                reject(new Error('IndexedDB is not supported'));
                return;
            }
            const request = indexedDB.open(DB_NAME, DB_VERSION);
            request.onupgradeneeded = function () {
                const db = request.result;
                if (!db.objectStoreNames.contains(STORE)) {
                    db.createObjectStore(STORE, { keyPath: 'key' });
                }
            };
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error || new Error('IndexedDB open failed'));
        });
    }

    async function put(payload, tenantKey) {
        const db = await openDb();
        const cacheKey = 'employee-roster-' + (tenantKey || 'default');
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, 'readwrite');
            tx.objectStore(STORE).put({ key: cacheKey, savedAt: Date.now(), payload });
            tx.oncomplete = () => { db.close(); resolve(true); };
            tx.onerror = () => { db.close(); reject(tx.error || new Error('Cache write failed')); };
        });
    }

    async function get(tenantKey) {
        const db = await openDb();
        const cacheKey = 'employee-roster-' + (tenantKey || 'default');
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, 'readonly');
            const request = tx.objectStore(STORE).get(cacheKey);
            request.onsuccess = () => {
                const value = request.result;
                db.close();
                if (!value || !Array.isArray(value.payload)) {
                    resolve(null);
                    return;
                }
                resolve({
                    payload: value.payload,
                    savedAt: Number(value.savedAt || 0),
                    stale: !value.savedAt || (Date.now() - value.savedAt) > MAX_AGE_MS
                });
            };
            request.onerror = () => { db.close(); reject(request.error || new Error('Cache read failed')); };
        });
    }

    async function clear(tenantKey) {
        const db = await openDb();
        const cacheKey = 'employee-roster-' + (tenantKey || 'default');
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, 'readwrite');
            tx.objectStore(STORE).delete(cacheKey);
            tx.oncomplete = () => { db.close(); resolve(true); };
            tx.onerror = () => { db.close(); reject(tx.error || new Error('Cache delete failed')); };
        });
    }

    function isOnline() {
        return navigator.onLine !== false;
    }

    let onlineHandler = null;
    function watchOnline(dotNetRef) {
        unwatchOnline();
        onlineHandler = function () {
            try {
                dotNetRef.invokeMethodAsync('EmployeeBrowserOnline');
            } catch (e) {
                console.debug('Employee online callback unavailable.', e);
            }
        };
        window.addEventListener('online', onlineHandler);
    }

    function unwatchOnline() {
        if (onlineHandler) {
            window.removeEventListener('online', onlineHandler);
            onlineHandler = null;
        }
    }

    return {
        save: put,
        load: get,
        clear: clear,
        isOnline: isOnline,
        watchOnline: watchOnline,
        unwatchOnline: unwatchOnline
    };
})();
