// Tutorial Guide - JS Interop for element positioning and spotlight
window.tutorialInterop = {
    /**
     * Get bounding rect of an element matching a CSS selector.
     * Returns { x, y, width, height } relative to viewport, or null if not found.
     */
    getElementRect: function (selector) {
        if (!selector) return null;
        var el = document.querySelector(selector);
        if (!el) return null;
        var rect = el.getBoundingClientRect();
        return {
            x: rect.x + window.scrollX,
            y: rect.y + window.scrollY,
            width: rect.width,
            height: rect.height,
            viewportX: rect.x,
            viewportY: rect.y
        };
    },

    /**
     * Scroll an element into view smoothly.
     */
    scrollToElement: function (selector) {
        if (!selector) return;
        var el = document.querySelector(selector);
        if (el) {
            el.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });
        }
    },

    /**
     * Listen for clicks on a specific element and invoke a .NET method when clicked.
     * Returns a listener ID that can be used to stop listening.
     */
    _listeners: {},
    _nextId: 1,

    listenForClick: function (selector, dotNetRef, methodName) {
        var id = this._nextId++;
        var handler = function () {
            dotNetRef.invokeMethodAsync(methodName);
        };
        // Use event delegation on document
        var delegateHandler = function (e) {
            var el = document.querySelector(selector);
            if (el && (el === e.target || el.contains(e.target))) {
                handler();
            }
        };
        document.addEventListener('click', delegateHandler, true);
        this._listeners[id] = delegateHandler;
        return id;
    },

    stopListening: function (listenerId) {
        var handler = this._listeners[listenerId];
        if (handler) {
            document.removeEventListener('click', handler, true);
            delete this._listeners[listenerId];
        }
    },

    /**
     * Get viewport dimensions.
     */
    getViewport: function () {
        return {
            width: window.innerWidth,
            height: window.innerHeight,
            scrollX: window.scrollX,
            scrollY: window.scrollY
        };
    }
};
