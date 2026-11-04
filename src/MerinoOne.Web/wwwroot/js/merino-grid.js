// R18 (2026-11-04) — resizable list-grid columns. A grid opts in with data-grid="{key}" on its .tl-grid-wrapper
// (see catalog-list.css, R18 block, and the GridResizeHint component). Dragging a header cell's right edge sets
// that column's width; the widths are remembered per grid key in this browser (localStorage "merino.grid.{key}").
//
// Blazor owns every element inside a page, so this script NEVER inserts, removes or re-attributes nodes in
// Blazor-rendered markup — its DOM diff would fight us. Everything dynamic goes through two kinds of <style>
// element that live in <head> and belong to this script ("tlgrid-{key}" = a grid's saved widths, "tlgrid-ui" =
// the transient hover/drag visuals), plus one badge element appended to <body> for the length of a drag. The
// saved-width rule uses !important so it beats the inline --tl-cols / --tl-min-width Blazor keeps re-rendering,
// while el.style.getPropertyValue('--tl-cols') still returns the page's own default template.
(function () {
    'use strict';
    if (window.__merinoGrid) return;
    window.__merinoGrid = true;

    var KEY_RE = /^[a-z0-9-]+$/;
    var STORAGE_PREFIX = 'merino.grid.';
    var RULE_PREFIX = 'tlgrid-';
    var UI_ID = 'tlgrid-ui';
    var HEADER_CELL = '.tl-grid-wrapper[data-grid] > .tlg-header > .tlg-col';
    var HANDLE_PX = 9;        // col-resize hit zone on a header cell's right edge
    var BADGE_INSET_PX = 12;  // width badge distance from the header cell's right edge
    var MIN_COL_PX = 60;      // narrowest a drag can make a column (a column that starts narrower keeps its width as its floor)

    var cache = new Map();    // key -> { cols, widths } | null; refreshed on every save / reset
    var drag = null;          // { key, index, startX, startW, widths, cols, cell, pointerId } while a drag is live
    var badge = null;
    var swallowClick = null;  // one-shot capture-phase click listener armed after a drag that changed a width
    var reconcilePending = false;

    // "ui" is excluded because the grid's rule element id ("tlgrid-ui") would collide with the visuals element.
    function validKey(key) {
        return typeof key === 'string' && KEY_RE.test(key) && key !== 'ui';
    }

    function isWidth(w) {
        return Number.isInteger(w) && w > 0;
    }

    function gridKey(cell) {
        return cell.parentElement.parentElement.getAttribute('data-grid');
    }

    function columnIndex(cell) {
        return Array.prototype.indexOf.call(cell.parentElement.children, cell);
    }

    function originalCols(wrapper) {
        return wrapper.style.getPropertyValue('--tl-cols').trim();
    }

    // ---- saved widths (localStorage behind a Map cache; blocked storage degrades to session-only) ----------

    function load(key) {
        if (cache.has(key)) return cache.get(key);
        var entry = null;
        try {
            var parsed = JSON.parse(localStorage.getItem(STORAGE_PREFIX + key));
            if (parsed && typeof parsed.cols === 'string' && Array.isArray(parsed.widths)
                && parsed.widths.length > 0 && parsed.widths.every(isWidth)) {
                entry = { cols: parsed.cols, widths: parsed.widths };
            }
        } catch {
            // storage blocked or the entry is unreadable — treat it as nothing saved
        }
        cache.set(key, entry);
        return entry;
    }

    function save(key, cols, widths) {
        var entry = { cols: cols, widths: widths.slice() };
        cache.set(key, entry);
        try {
            localStorage.setItem(STORAGE_PREFIX + key, JSON.stringify(entry));
        } catch {
            // private mode / quota — the cache still remembers it for this session
        }
    }

    // Forgets a grid's saved widths: the page's own columns (and the hidden "saved" note) come back.
    function reset(key) {
        cache.set(key, null);
        try {
            localStorage.removeItem(STORAGE_PREFIX + key);
        } catch {
            // blocked storage — nothing was persisted
        }
        setStyle(RULE_PREFIX + key, '');
    }

    // ---- owned <style> elements -----------------------------------------------------------------------------

    // Creates, updates or removes the element; writes only when the text differs so the MutationObserver
    // below cannot feed on its own edits.
    function setStyle(id, text) {
        var el = document.getElementById(id);
        if (!text) {
            if (el) el.remove();
            return;
        }
        if (!el) {
            el = document.createElement('style');
            el.id = id;
            document.head.appendChild(el);
        }
        if (el.textContent !== text) el.textContent = text;
    }

    // withNote reveals the hint's "Custom widths saved" line; a live drag passes false until something is stored,
    // so the note never shows for widths that have not been saved (or flashes when a drag ends where it began).
    function widthRule(key, widths, withNote) {
        var sum = widths.reduce(function (a, b) { return a + b; }, 0);
        var cols = widths.map(function (w) { return w + 'px'; }).join(' ');
        return '.tl-grid-wrapper[data-grid="' + key + '"]{--tl-cols:' + cols + ' !important;--tl-min-width:' + sum + 'px !important;}'
            + (withNote ? '.tl-grid-hint[data-grid-hint="' + key + '"] .tl-grid-hint__saved{display:inline;}' : '');
    }

    // Puts a grid's rule back to what is stored (the saved widths, or no rule at all).
    function applyStored(key) {
        var entry = load(key);
        setStyle(RULE_PREFIX + key, entry ? widthRule(key, entry.widths, true) : '');
    }

    function headerCellSelector(key, nth) {
        return '.tl-grid-wrapper[data-grid="' + key + '"] > .tlg-header > .tlg-col:nth-child(' + nth + ')';
    }

    function hoverRule(cell) {
        return headerCellSelector(gridKey(cell), columnIndex(cell) + 1) + '::after{opacity:1;}';
    }

    function dragRule(key, nth) {
        return 'html,html *{cursor:col-resize !important;user-select:none !important;}'
            + headerCellSelector(key, nth) + '::after{opacity:1;}'
            + '.tl-grid-wrapper[data-grid="' + key + '"] > .tlg-row > .tlg-col:nth-child(' + nth + '){box-shadow:inset -2px 0 0 var(--mer-blue);}';
    }

    // ---- resize handle --------------------------------------------------------------------------------------

    // The opted-in header cell whose right-edge hit zone the pointer is over, or null.
    function handleCell(e) {
        if (!(e.target instanceof Element)) return null;
        var cell = e.target.closest(HEADER_CELL);
        if (!cell || e.clientX < cell.getBoundingClientRect().right - HANDLE_PX) return null;
        return validKey(gridKey(cell)) ? cell : null;
    }

    function placeBadge() {
        var rect = drag.cell.getBoundingClientRect();
        badge.textContent = drag.widths[drag.index] + ' px';
        badge.style.left = (rect.right - BADGE_INSET_PX) + 'px';
        badge.style.top = (rect.top + rect.height / 2) + 'px';
    }

    function startDrag(cell, e) {
        var header = cell.parentElement;
        var widths = Array.prototype.map.call(header.children, function (c) {
            return Math.round(c.getBoundingClientRect().width);
        });
        var index = columnIndex(cell);
        drag = {
            key: gridKey(cell),
            index: index,
            startX: e.clientX,
            startW: widths[index],
            widths: widths,
            cols: originalCols(header.parentElement),
            cell: cell,
            pointerId: e.pointerId
        };
        try {
            cell.setPointerCapture(e.pointerId);
        } catch {
            // a pointer that is not active (synthetic / already released) cannot be captured — the document-level
            // listeners still follow the drag
        }
        setStyle(UI_ID, dragRule(drag.key, index + 1));
        badge = document.createElement('div');
        badge.className = 'tl-grid-badge';
        document.body.appendChild(badge);
        placeBadge();
    }

    function endDrag() {
        if (!drag) return;
        var d = drag;
        drag = null;
        if (d.cell.hasPointerCapture(d.pointerId)) d.cell.releasePointerCapture(d.pointerId);
        badge.remove();
        badge = null;
        setStyle(UI_ID, '');
        if (d.widths[d.index] === d.startW) {
            applyStored(d.key);
            return;
        }
        save(d.key, d.cols, d.widths);
        applyStored(d.key);
        // The click that ends this gesture would otherwise reach the header cell (sorting etc.).
        swallowClick = function (e) {
            disarmSwallow();
            e.stopPropagation();
            e.preventDefault();
        };
        window.addEventListener('click', swallowClick, true);
    }

    function disarmSwallow() {
        if (!swallowClick) return;
        window.removeEventListener('click', swallowClick, true);
        swallowClick = null;
    }

    document.addEventListener('pointerdown', function (e) {
        disarmSwallow();
        if (drag || e.button !== 0) return;
        var cell = handleCell(e);
        if (!cell) return;
        e.preventDefault();
        startDrag(cell, e);
    });

    document.addEventListener('pointermove', function (e) {
        if (!drag) {
            var cell = handleCell(e);
            setStyle(UI_ID, cell ? hoverRule(cell) : '');
            return;
        }
        drag.widths[drag.index] = Math.max(Math.min(MIN_COL_PX, drag.startW), Math.round(drag.startW + e.clientX - drag.startX));
        setStyle(RULE_PREFIX + drag.key, widthRule(drag.key, drag.widths, load(drag.key) !== null));
        placeBadge();
    });

    // Leaving the window leaves no pointermove behind, so clear the hover bar here.
    document.addEventListener('pointerout', function (e) {
        if (!drag && !e.relatedTarget) setStyle(UI_ID, '');
    });

    document.addEventListener('pointerup', endDrag);
    document.addEventListener('pointercancel', endDrag);
    document.addEventListener('lostpointercapture', endDrag);

    document.addEventListener('dblclick', function (e) {
        var cell = handleCell(e);
        if (cell) reset(gridKey(cell));
    });

    // ---- reconcile saved widths with the grids currently on the page -----------------------------------------

    // Pages render their grid only once the data has loaded and Blazor enhanced navigation swaps pages without a
    // reload, so the saved rules are (re)checked whenever the DOM changes rather than once at start.
    function reconcile() {
        reconcilePending = false;
        var wrappers = document.querySelectorAll('.tl-grid-wrapper[data-grid]');
        for (var i = 0; i < wrappers.length; i++) {
            var wrapper = wrappers[i];
            var key = wrapper.getAttribute('data-grid');
            if (!validKey(key) || (drag && drag.key === key)) continue;
            var header = wrapper.querySelector(':scope > .tlg-header');
            if (!header || header.children.length === 0) continue;
            var entry = load(key);
            if (!entry) continue;
            if (entry.cols === originalCols(wrapper) && entry.widths.length === header.children.length) {
                setStyle(RULE_PREFIX + key, widthRule(key, entry.widths, true));
            } else {
                // the page changed its columns, so the widths no longer describe this grid
                reset(key);
            }
        }
    }

    // setTimeout, not requestAnimationFrame: rAF does not run in hidden tabs.
    function scheduleReconcile() {
        if (reconcilePending) return;
        reconcilePending = true;
        setTimeout(reconcile, 0);
    }

    new MutationObserver(scheduleReconcile).observe(document.documentElement, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ['style', 'data-grid']
    });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', reconcile);
    } else {
        reconcile();
    }
})();
