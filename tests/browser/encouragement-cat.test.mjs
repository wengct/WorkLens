import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

// Load the browser ES module without changing the repository's Node module type.
const source = await readFile(new URL('../../wwwroot/js/encouragement-cat.js', import.meta.url), 'utf8');
const { createCycle, createCatMotion, contentArea, edgeCandidates, choosePosition, bubblePlacement } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function setup({ safe = true } = {}) {
    let time = 0, sequence = 0;
    const pending = new Map();
    const events = [];
    const clock = {
        setTimeout(fn, delay) { const id = ++sequence; pending.set(id, { fn, at: time + delay }); return id; },
        clearTimeout(id) { pending.delete(id); },
        advance(ms) {
            const end = time + ms;
            while (true) {
                const next = [...pending].sort((a, b) => a[1].at - b[1].at)[0];
                if (!next || next[1].at > end) break;
                time = next[1].at;
                pending.delete(next[0]);
                next[1].fn();
            }
            time = end;
        }
    };
    const view = Object.fromEntries(['hide', 'hidden', 'idle', 'open', 'close', 'suspend'].map(name =>
        [name, value => events.push([name, value])]));
    view.show = () => { events.push(['show']); return safe; };
    const cycle = createCycle(view, clock, () => 0);
    return { cycle, clock, events, pending, setSafe(value) { safe = value; } };
}

test('playful idle actions vary and never repeat back to back', () => {
    const root = { dataset: {} };
    const motion = createCatMotion(root, () => false, () => 0);
    motion.idle();
    assert.equal(root.dataset.play, 'blink');
    motion.idle();
    assert.equal(root.dataset.play, 'look');
    motion.idle();
    assert.equal(root.dataset.play, 'blink');
    createCatMotion(root, () => false, () => .99).idle();
    assert.equal(root.dataset.play, 'wiggle');
});

test('approach or suspension cancels a performance without leaving motion state', () => {
    const root = { dataset: {} };
    const motion = createCatMotion(root);
    for (const action of ['peek', 'look', 'wiggle', 'found']) {
        motion.play(action);
        motion.stop();
        assert.equal(root.dataset.play, undefined);
    }
});

test('a stale animation end cannot cancel the greeting that replaced it', () => {
    const root = { dataset: {} };
    const motion = createCatMotion(root);
    motion.play('peek');
    motion.play('found');
    motion.finish('cat-double-peek');
    motion.finish('cat-sparkle');
    assert.equal(root.dataset.play, 'found');
    motion.finish('cat-paw-wave');
    assert.equal(root.dataset.play, undefined);
});

test('reduced motion suppresses both arrival and greeting performances', () => {
    const root = { dataset: {} };
    const motion = createCatMotion(root, () => true);
    motion.play('peek');
    assert.equal(root.dataset.play, undefined);
    motion.play('found');
    assert.equal(root.dataset.play, undefined);
    motion.idle();
    assert.equal(root.dataset.play, undefined);
});

test('first appearance, idle animation, hide and reappear use the expected delays', () => {
    const { cycle, clock, events } = setup();
    cycle.start();
    clock.advance(4999);
    assert.equal(events.length, 0);
    clock.advance(1);
    assert.equal(cycle.phase, 'visible');
    clock.advance(10000);
    assert.equal(events.at(-1)[0], 'idle');
    clock.advance(20000);
    assert.equal(cycle.phase, 'hiding');
    clock.advance(400);
    assert.equal(cycle.phase, 'hidden');
    clock.advance(5000);
    assert.equal(cycle.phase, 'visible');
});

test('hover or keyboard focus pauses movement and leaving starts a fresh dwell', () => {
    const { cycle, clock } = setup();
    cycle.start(); clock.advance(5000);
    cycle.setEngaged(true); clock.advance(120000);
    assert.equal(cycle.phase, 'visible');
    cycle.setEngaged(false); clock.advance(29999);
    assert.equal(cycle.phase, 'visible');
    clock.advance(1); assert.equal(cycle.phase, 'hiding');
});

test('reading survives suspension and close waits for focus to leave', () => {
    const { cycle, clock } = setup();
    cycle.start(); clock.advance(5000); cycle.open();
    cycle.setSuspended(true); clock.advance(120000);
    cycle.setSuspended(false); clock.advance(120000);
    assert.equal(cycle.phase, 'open');
    cycle.setEngaged(true); cycle.close(); clock.advance(120000);
    assert.equal(cycle.phase, 'visible');
    cycle.setEngaged(false);
    assert.equal(cycle.phase, 'hiding');
});

test('background pauses startup and rest or disposal cancels every timer', () => {
    for (const action of ['rest', 'dispose']) {
        const { cycle, clock, events, pending } = setup();
        cycle.start(); cycle.setSuspended(true); clock.advance(120000);
        assert.equal(events.some(event => event[0] === 'show'), false);
        cycle.setSuspended(false); clock.advance(5000);
        assert.equal(cycle.phase, 'visible');
        cycle[action]();
        assert.equal(pending.size, 0);
        const count = events.length;
        clock.advance(120000);
        assert.equal(events.length, count);
    }
});

test('no safe anchor retries after ten seconds', () => {
    const { cycle, clock, events, setSafe } = setup({ safe: false });
    cycle.start(); clock.advance(5000);
    assert.equal(cycle.phase, 'hidden');
    clock.advance(9999); assert.equal(events.length, 1);
    setSafe(true); clock.advance(1);
    assert.equal(cycle.phase, 'visible');
});

test('reduced motion keeps a fixed entry with no idle animation or automatic move', () => {
    const { cycle, clock, events, pending } = setup();
    cycle.setReduced(true); cycle.start(); clock.advance(5000);
    clock.advance(120000);
    assert.deepEqual(events, [['show']]);
    cycle.open(); cycle.close(); clock.advance(120000);
    assert.equal(cycle.phase, 'visible');
    assert.equal(pending.size, 0);
    cycle.setReduced(false); clock.advance(30000);
    assert.equal(cycle.phase, 'hiding');
});

test('new obstacles only relocate an idle cat, never one being read or focused', () => {
    const { cycle, clock } = setup();
    cycle.start(); clock.advance(5000);
    cycle.setEngaged(true); cycle.relocate();
    assert.equal(cycle.phase, 'visible');
    cycle.open(); cycle.relocate();
    assert.equal(cycle.phase, 'open');
    cycle.close(); cycle.setEngaged(false);
    clock.advance(5400);
    assert.equal(cycle.phase, 'visible');
    cycle.relocate();
    assert.equal(cycle.phase, 'hiding');
});

test('suspension during retreat resumes without leaving the cat stuck offscreen', () => {
    const { cycle, clock } = setup();
    cycle.start(); clock.advance(35000);
    assert.equal(cycle.phase, 'hiding');
    cycle.setSuspended(true); clock.advance(120000);
    cycle.setSuspended(false); clock.advance(5000);
    assert.equal(cycle.phase, 'visible');
});

test('desktop and phone have four edges with anchors inside the safe viewport', () => {
    for (const width of [320, 650, 900, 1440]) {
        const vp = { left: 5, top: 10, width, height: 568 };
        const candidates = edgeCandidates(vp, { top: 24, bottom: 20, left: 8, right: 8 });
        assert.equal(new Set(candidates.map(item => item.side)).size, 4);
        assert.equal(new Set(candidates.map(item => item.id)).size, candidates.length);
        for (const item of candidates) {
            assert.ok(item.left >= 13 && item.left + 88 <= width - 3 + .001);
            assert.ok(item.top >= 34 && item.top + 88 <= 558 + .001);
        }
    }
    assert.deepEqual(edgeCandidates({ left: 0, top: 0, width: 70, height: 70 }), []);
});

test('blocked sidebar does not trap the cat on the right and direction takes precedence over anchor count', () => {
    const candidates = edgeCandidates({ left: 0, top: 0, width: 1440, height: 900 });
    const safe = candidates.filter(item => item.side !== 'left');
    const previous = safe.find(item => item.side === 'right');
    for (const value of [0, .25, .5, .99]) {
        const chosen = choosePosition(safe, previous, false, () => value);
        assert.ok(['top', 'bottom'].includes(chosen.side));
        assert.notEqual(chosen.id, previous.id);
    }
    assert.equal(choosePosition(safe, previous, true).id, previous.id);
    assert.equal(choosePosition([previous], previous, false), undefined);
    assert.equal(choosePosition([], previous, false), undefined);
});

test('bubble and directional tail fit every anchor including narrow and short screens', () => {
    for (const [width, height] of [[1440, 900], [900, 740], [650, 740], [320, 568], [320, 200]]) {
        const vp = { left: 0, top: 0, width, height };
        const bubbleWidth = Math.min(336, width - 24);
        for (const anchor of edgeCandidates(vp)) {
            const placement = bubblePlacement(vp, anchor, bubbleWidth, 310);
            assert.ok(placement.left >= 12 && placement.left + bubbleWidth <= width - 12 + .001);
            assert.ok(placement.top >= 12 && placement.top + Math.min(310, placement.maxHeight) <= height - 12 + .001);
            if (placement.tailSide !== 'none') {
                assert.ok(placement.tailOffset >= 26);
                const bubble = { left: placement.left, right: placement.left + bubbleWidth, top: placement.top, bottom: placement.top + Math.min(310, placement.maxHeight) };
                assert.ok(bubble.right <= anchor.left || bubble.left >= anchor.left + 88 || bubble.bottom <= anchor.top || bubble.top >= anchor.top + 88);
            }
        }
    }
});

test('top cat speaks downward and bottom cat speaks upward', () => {
    const vp = { left: 0, top: 0, width: 900, height: 740 };
    assert.equal(bubblePlacement(vp, { side: 'top', left: 400, top: 0 }, 336, 300).tailSide, 'top');
    assert.equal(bubblePlacement(vp, { side: 'bottom', left: 400, top: 652 }, 336, 300).tailSide, 'bottom');
});

test('all activity stays in the visible content container, away from sidebar and outside margins', () => {
    const vp = { left: 0, top: 0, width: 1440, height: 900 };
    const area = contentArea(vp, { left: 268, top: 20, right: 1400, bottom: 1600 });
    assert.deepEqual(area, { left: 268, top: 20, width: 1132, height: 880 });
    for (const anchor of edgeCandidates(area)) {
        assert.ok(anchor.left >= 268 && anchor.left + 88 <= 1400 + .001);
        assert.ok(anchor.top >= 20 && anchor.top + 88 <= 900 + .001);
        const bubble = bubblePlacement(area, anchor, 336, 310);
        assert.ok(bubble.left >= 280 && bubble.left + 336 <= 1388 + .001);
        assert.ok(bubble.top >= 32 && bubble.top + Math.min(310, bubble.maxHeight) <= 888 + .001);
    }
});

test('scrolled content and mobile safe areas are intersected, and offscreen content has no candidates', () => {
    const vp = { left: 0, top: 0, width: 320, height: 568 };
    const area = contentArea(vp, { left: 12, top: -200, right: 308, bottom: 900 }, { top: 24, bottom: 20 });
    assert.deepEqual(area, { left: 12, top: 24, width: 296, height: 524 });
    const hidden = contentArea(vp, { left: 12, top: 700, right: 308, bottom: 1400 });
    assert.deepEqual(edgeCandidates(hidden), []);
});
