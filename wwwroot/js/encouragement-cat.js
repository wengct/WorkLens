// The cycle has an injectable clock so interaction timing can be tested without waiting.
export function createCycle(view, clock = globalThis, random = Math.random) {
    let phase = "hidden", suspended = false, engaged = false, reduced = false;
    let stopped = false, closePending = false, timer, idleTimer;
    const delay = (min, max) => min + random() * (max - min);
    function clear() {
        clock.clearTimeout(timer);
        clock.clearTimeout(idleTimer);
    }
    function schedule(action, ms) {
        clear();
        if (!stopped && !suspended) timer = clock.setTimeout(action, ms);
    }
    function idle() {
        if (stopped || suspended || engaged || reduced || phase !== "visible") return;
        idleTimer = clock.setTimeout(() => { view.idle(); idle(); }, delay(10000, 20000));
    }
    function dwell() {
        clear();
        if (engaged || reduced || stopped || suspended || phase !== "visible") return;
        schedule(hide, delay(30000, 60000));
        idle();
    }
    function show() {
        if (stopped || suspended) return;
        if (!view.show()) {
            phase = "hidden";
            schedule(show, 10000);
            return;
        }
        phase = "visible";
        dwell();
    }
    function hide() {
        clear();
        if (stopped || suspended || engaged || phase === "open") return;
        closePending = false;
        phase = "hiding";
        view.hide();
        schedule(() => {
            phase = "hidden";
            view.hidden();
            schedule(show, delay(5000, 10000));
        }, reduced ? 0 : 400);
    }
    return {
        get phase() { return phase; },
        start() { schedule(show, 5000); },
        setEngaged(value) {
            engaged = value;
            if (value && phase === "visible") clear();
            else if (closePending && !reduced) hide();
            else if (phase === "visible") dwell();
        },
        setSuspended(value) {
            if (value === suspended || stopped) return;
            suspended = value;
            clear();
            view.suspend(value);
            if (!value) {
                if (phase === "visible") dwell();
                else if (phase !== "open") schedule(show, 5000);
            }
        },
        setReduced(value) {
            reduced = value;
            if (phase === "visible") dwell();
        },
        open() {
            if (stopped || suspended || (phase !== "visible" && phase !== "open")) return;
            clear();
            closePending = false;
            phase = "open";
            view.open();
        },
        close() {
            if (phase !== "open") return;
            phase = "visible";
            closePending = !reduced;
            view.close();
            if (!engaged && !reduced) hide();
        },
        relocate() {
            if (phase === "visible" && !engaged) hide();
        },
        rest() {
            stopped = true;
            clear();
            phase = "hidden";
            view.close();
            view.hidden();
        },
        dispose() { stopped = true; clear(); }
    };
}

// CSS owns the short performances; no extra timers survive a pause or disposal.
export function createCatMotion(root, reduced = () => false, random = Math.random) {
    const endings = { peek: "cat-double-peek", blink: "cat-blink", look: "cat-look-around", wiggle: "cat-head-tilt", found: "cat-paw-wave" };
    let previousIdle;
    function stop() { delete root.dataset.play; }
    function play(action) {
        stop();
        if (!reduced()) root.dataset.play = action;
    }
    return {
        play,
        stop,
        idle() {
            const choices = ["blink", "look", "wiggle"].filter(action => action !== previousIdle);
            previousIdle = choices[Math.floor(random() * choices.length)];
            play(previousIdle);
        },
        finish(name) {
            if (name === endings[root.dataset.play]) stop();
        }
    };
}

export function contentArea(viewport, content, safe = {}) {
    const left = Math.max(content.left, viewport.left + (safe.left ?? 0));
    const top = Math.max(content.top, viewport.top + (safe.top ?? 0));
    const right = Math.min(content.right, viewport.left + viewport.width - (safe.right ?? 0));
    const bottom = Math.min(content.bottom, viewport.top + viewport.height - (safe.bottom ?? 0));
    return { left, top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
}

export function edgeCandidates(vp, safe = {}) {
    const left = vp.left + (safe.left ?? 0);
    const top = vp.top + (safe.top ?? 0);
    const right = vp.left + vp.width - 88 - (safe.right ?? 0);
    const bottom = vp.top + vp.height - 88 - (safe.bottom ?? 0);
    if (right < left || bottom < top) return [];
    const points = (start, end) => {
        const count = Math.max(1, Math.ceil((end - start) / 88));
        return Array.from({ length: count + 1 }, (_, index) => start + (end - start) * index / count);
    };
    return [
        ...["left", "right"].flatMap(side => points(top, bottom).map((y, index) =>
            ({ id: `${side}-${index}`, side, left: side === "left" ? left : right, top: y }))),
        ...["top", "bottom"].flatMap(side => points(left, right).map((x, index) =>
            ({ id: `${side}-${index}`, side, left: x, top: side === "top" ? top : bottom })))
    ];
}

export function choosePosition(available, previous, reduced, random = Math.random) {
    if (reduced) {
        const current = available.find(item => item.id === previous?.id);
        if (current) return current;
    }
    const alternatives = available.filter(item => item.id !== previous?.id);
    const differentSides = alternatives.filter(item => item.side !== previous?.side);
    const pool = differentSides.length ? differentSides : alternatives;
    // Choose a direction first, so wide screens do not unfairly favor top/bottom.
    const sides = [...new Set(pool.map(item => item.side))];
    const side = sides[Math.floor(random() * sides.length)];
    const positions = pool.filter(item => item.side === side);
    return positions[Math.floor(random() * positions.length)];
}

export function bubblePlacement(vp, anchor, width, height) {
    const gap = 14, margin = 12;
    const clamp = (value, min, max) => Math.max(min, Math.min(value, max));
    const centerX = anchor.left + 44, centerY = anchor.top + 44;
    const spaces = {
        above: anchor.top - vp.top - margin - gap,
        below: vp.top + vp.height - margin - anchor.top - 88 - gap,
        left: anchor.left - vp.left - margin - gap,
        right: vp.left + vp.width - margin - anchor.left - 88 - gap
    };
    const preferred = { top: "below", bottom: "above", left: "right", right: "left" }[anchor.side];
    const order = [...new Set([preferred, "above", "below", "left", "right"])];
    let direction = order.find(side => spaces[side] >= (side === "above" || side === "below" ? height : width));
    let maxHeight = vp.height - margin * 2;
    if (!direction) {
        direction = spaces.above >= spaces.below ? "above" : "below";
        if (spaces[direction] >= 140) maxHeight = spaces[direction];
        else direction = "overlap"; // Very short viewports: prioritize reachable, scrollable content.
    }
    const actualHeight = Math.min(height, maxHeight);
    let left = centerX - width / 2, top = centerY - actualHeight / 2;
    if (direction === "above") top = anchor.top - gap - actualHeight;
    if (direction === "below") top = anchor.top + 88 + gap;
    if (direction === "left") left = anchor.left - gap - width;
    if (direction === "right") left = anchor.left + 88 + gap;
    left = clamp(left, vp.left + margin, vp.left + vp.width - margin - width);
    top = clamp(top, vp.top + margin, vp.top + vp.height - margin - actualHeight);
    const tailSide = { above: "bottom", below: "top", left: "right", right: "left", overlap: "none" }[direction];
    const horizontal = tailSide === "top" || tailSide === "bottom";
    const tailOffset = clamp(horizontal ? centerX - left : centerY - top, 26, (horizontal ? width : actualHeight) - 26);
    return { left, top, maxHeight, tailSide, tailOffset };
}

const instances = new WeakMap();
const storageKey = "worklens.encouragement-cat.rest";
const controls = "button, a[href], input, textarea, select, summary, [contenteditable]:not([contenteditable='false']), [role='button'], [role='textbox'], .CodeMirror, .editor-toolbar";
const overlays = "dialog[open], [aria-modal='true'], .modal-backdrop, .processing-overlay";
const intersects = (a, b) => a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;

export function initialize(root) {
    if (instances.has(root)) return;
    const trigger = root.querySelector("[data-cat-trigger]");
    const bubble = root.querySelector(".encouragement-cat-bubble");
    const surface = root.querySelector(".encouragement-cat-surface");
    const hint = root.querySelector(".encouragement-cat-hint");
    const container = root.closest(".content-container");
    const motion = matchMedia("(prefers-reduced-motion: reduce)");
    const performance = createCatMotion(root, () => motion.matches);
    const abort = new AbortController();
    let lastPosition, position, hovered = false, disposed = false, frame = 0, bubbleTimer;
    const on = (target, name, handler, options = {}) => target.addEventListener(name, handler, { ...options, signal: abort.signal });

    function viewport() {
        const visual = window.visualViewport;
        return { left: visual?.offsetLeft ?? 0, top: visual?.offsetTop ?? 0,
            width: visual?.width ?? document.documentElement.clientWidth,
            height: visual?.height ?? innerHeight };
    }
    function activityArea() {
        const style = getComputedStyle(root);
        const safe = name => parseFloat(style.getPropertyValue(name)) || 0;
        return contentArea(viewport(), container.getBoundingClientRect(),
            Object.fromEntries(["top", "bottom", "left", "right"].map(side => [side, safe(`--cat-safe-${side}`)])));
    }
    function candidates() {
        return edgeCandidates(activityArea());
    }
    function bounds(candidate) {
        return { left: candidate.left - 4, right: candidate.left + 92, top: candidate.top - 4, bottom: candidate.top + 92 };
    }
    function isVisible(element) {
        const style = getComputedStyle(element);
        return element.getClientRects().length > 0 && style.visibility !== "hidden" && style.display !== "none";
    }
    function isSafe(candidate) {
        const area = bounds(candidate);
        const vp = activityArea();
        if (candidate.left < vp.left - .1 || candidate.top < vp.top - .1 || candidate.left + 88 > vp.left + vp.width + .1 || candidate.top + 88 > vp.top + vp.height + .1) return false;
        return !Array.from(document.querySelectorAll(controls)).some(element =>
            !root.contains(element) && isVisible(element) && intersects(area, element.getBoundingClientRect()));
    }
    function place(candidate) {
        position = candidate;
        root.dataset.side = candidate.side;
        root.style.left = `${candidate.left}px`;
        root.style.top = `${candidate.top}px`;
        const area = activityArea();
        const hintLeft = Math.max(area.left + 4, Math.min(candidate.left, area.left + area.width - hint.offsetWidth - 4));
        const hintTop = candidate.top - hint.offsetHeight - 6 >= area.top
            ? candidate.top - hint.offsetHeight - 6 : candidate.top + 94;
        hint.style.left = `${hintLeft - candidate.left}px`;
        hint.style.right = "auto";
        hint.style.bottom = "auto";
        hint.style.top = `${Math.max(area.top, Math.min(hintTop, area.top + area.height - hint.offsetHeight)) - candidate.top}px`;
    }
    function fitBubble() {
        if (bubble.hidden || !position) return;
        const vp = activityArea();
        bubble.style.width = `${Math.min(336, vp.width - 24)}px`;
        surface.style.maxHeight = `${Math.max(44, vp.height - 24)}px`;
        // Measure layout size, not the temporarily scaled animation rectangle.
        const width = bubble.offsetWidth;
        const height = bubble.offsetHeight;
        const placement = bubblePlacement(vp, position, width, height);
        surface.style.maxHeight = `${placement.maxHeight}px`;
        bubble.style.left = `${placement.left - position.left}px`;
        bubble.style.top = `${placement.top - position.top}px`;
        bubble.dataset.tail = placement.tailSide;
        bubble.style.setProperty("--tail-offset", `${placement.tailOffset}px`);
    }
    function setState(state) {
        root.dataset.state = state;
        trigger.disabled = state === "hidden" || state === "hiding";
    }
    const cycle = createCycle({
        show() {
            const available = candidates().filter(isSafe);
            const chosen = choosePosition(available, lastPosition, motion.matches);
            if (!chosen) { setState("hidden"); return false; }
            place(chosen);
            lastPosition = chosen;
            setState("visible");
            performance.play("peek");
            return true;
        },
        hide() { performance.stop(); setState("hiding"); },
        hidden() { performance.stop(); setState("hidden"); },
        idle() { performance.idle(); },
        suspend(value) {
            if (value) performance.stop();
            root.dataset.suspended = String(value);
            root.inert = value;
        },
        open() {
            clearTimeout(bubbleTimer);
            root.classList.remove("cat-bubble-closing");
            bubble.hidden = false;
            bubble.inert = false;
            trigger.setAttribute("aria-expanded", "true");
            setState("open");
            performance.play("found");
            fitBubble();
        },
        close() {
            performance.stop();
            trigger.setAttribute("aria-expanded", "false");
            bubble.inert = true;
            root.classList.add("cat-bubble-closing");
            bubbleTimer = setTimeout(() => { bubble.hidden = true; root.classList.remove("cat-bubble-closing"); }, motion.matches ? 0 : 200);
            setState("visible");
        }
    });
    function engage() {
        const engaged = hovered || root.contains(document.activeElement);
        if (engaged && cycle.phase === "visible") performance.stop();
        cycle.setEngaged(engaged);
    }
    function close() {
        trigger.focus({ preventScroll: true });
        engage();
        cycle.close();
    }
    function check() {
        frame = 0;
        if (!root.isConnected) { dispose(root); return; }
        const area = activityArea();
        const blocked = document.hidden || area.width < 112 || area.height < 112
            || Array.from(document.querySelectorAll(overlays)).some(isVisible);
        cycle.setSuspended(blocked);
        if (blocked) { hovered = false; engage(); return; }
        if (position) {
            const updated = candidates().find(candidate => candidate.id === position.id)
                ?? candidates().find(candidate => candidate.side === position.side);
            if (updated) place(updated);
            fitBubble();
            if (cycle.phase === "visible" && !isSafe(position)) cycle.relocate();
        }
    }
    function queueCheck() { if (!disposed && !frame) frame = requestAnimationFrame(check); }
    on(trigger, "click", () => cycle.open());
    root.querySelectorAll("[data-cat-close]").forEach(button => on(button, "click", close));
    on(root.querySelector("[data-cat-rest]"), "click", () => {
        try { sessionStorage.setItem(storageKey, "true"); } catch { /* Rest still applies to this instance. */ }
        // Move focus out before hiding the companion; never strand it on a hidden button.
        const destination = document.querySelector("main h1, main, .main-content");
        if (destination) {
            const old = destination.getAttribute("tabindex");
            destination.setAttribute("tabindex", "-1");
            destination.focus({ preventScroll: true });
            if (old === null) destination.removeAttribute("tabindex"); else destination.setAttribute("tabindex", old);
        }
        cycle.rest();
    });
    on(root, "pointerenter", event => { if (event.pointerType !== "touch") { hovered = true; engage(); } });
    on(root, "pointerleave", () => { hovered = false; engage(); });
    on(root, "focusin", engage);
    on(root, "focusout", () => queueMicrotask(() => { if (!disposed) engage(); }));
    on(root, "keydown", event => {
        if (event.key === "Escape" && cycle.phase === "open") { event.preventDefault(); event.stopPropagation(); close(); }
    });
    on(root, "animationend", event => performance.finish(event.animationName));
    on(document, "visibilitychange", queueCheck);
    on(document, "scroll", queueCheck, { capture: true, passive: true });
    on(window, "resize", queueCheck, { passive: true });
    if (window.visualViewport) {
        on(window.visualViewport, "resize", queueCheck);
        on(window.visualViewport, "scroll", queueCheck);
    }
    on(motion, "change", () => { performance.stop(); cycle.setReduced(motion.matches); queueCheck(); });
    const observer = new MutationObserver(records => {
        if (records.some(record => !root.contains(record.target))) queueCheck();
    });
    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["class", "style", "hidden", "open", "aria-modal"] });
    const resizeObserver = new ResizeObserver(queueCheck);
    resizeObserver.observe(bubble);
    resizeObserver.observe(container);
    instances.set(root, {
        refresh: queueCheck,
        dispose() {
            disposed = true;
            performance.stop();
            cycle.dispose();
            abort.abort();
            observer.disconnect();
            resizeObserver.disconnect();
            cancelAnimationFrame(frame);
            clearTimeout(bubbleTimer);
        }
    });
    cycle.setReduced(motion.matches);
    let resting = false;
    try { resting = sessionStorage.getItem(storageKey) === "true"; } catch { }
    if (resting) cycle.rest(); else { cycle.start(); check(); }
}

export function refresh(root) { instances.get(root)?.refresh(); }
export function dispose(root) { instances.get(root)?.dispose(); instances.delete(root); }
