// kb-graph-drag.js
// Pure-JS drag for the floating Knowledge Graph window.
// Called from Blazor via initDrag() AFTER the DOM element exists.
// Never uses Blazor event callbacks — avoids the server round-trip
// that caused stale coordinates and the window teleporting on first drag.

window.KbGraphDrag = (() => {

    function initDrag(windowId, headerId) {
        const win = document.getElementById(windowId);
        const header = document.getElementById(headerId);
        if (!win || !header) return;

        // Remove any previously attached listener to avoid duplicates on re-render
        header._dragListener && header.removeEventListener('mousedown', header._dragListener);

        function onMouseDown(e) {
            if (e.button !== 0) return;        // left-click only
            e.preventDefault();

            // Read the window's CURRENT screen position live from the DOM.
            // This is the key fix: we never trust the Blazor-passed coordinates.
            const rect = win.getBoundingClientRect();
            const startX = e.clientX;
            const startY = e.clientY;
            const startL = rect.left;
            const startT = rect.top;

            document.body.style.userSelect = 'none';

            function onMouseMove(mv) {
                const newLeft = Math.max(0, startL + (mv.clientX - startX));
                const newTop = Math.max(0, startT + (mv.clientY - startY));
                win.style.left = newLeft + 'px';
                win.style.top = newTop + 'px';
            }

            function onMouseUp() {
                document.removeEventListener('mousemove', onMouseMove);
                document.removeEventListener('mouseup', onMouseUp);
                document.body.style.userSelect = '';
            }

            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
        }

        header._dragListener = onMouseDown;
        header.addEventListener('mousedown', onMouseDown);
    }

    return { initDrag };
})();
