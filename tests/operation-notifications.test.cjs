const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../wwwroot/js/operation-notifications.js'), 'utf8');

function browser({ hidden = true, focused = false, permission = 'granted', preference = 'enabled', storageFails = false, supported = true } = {}) {
    const events = {}, sent = [], writes = [];
    let observer;
    const document = { title: '工作摘要｜WorkLens', head: {}, hidden, hasFocus: () => focused,
        addEventListener: (name, callback) => events[name] = callback };
    class Notification {
        static permission = permission;
        static requests = 0;
        static requestPermission() { Notification.requests++; return Promise.resolve(Notification.permission); }
        constructor(title, options) { this.title = title; this.options = options; sent.push(this); }
        close() { this.closed = true; }
    }
    const window = { isSecureContext: true, Notification,
        addEventListener: (name, callback) => events[name] = callback, focus: () => focused = true };
    if (!supported) delete window.Notification;
    vm.runInNewContext(source, { window, document, Notification,
        localStorage: { getItem: () => { if (storageFails) throw Error(); return preference; }, setItem: (k, v) => { if (storageFails) throw Error(); writes.push(v); } },
        MutationObserver: class { constructor(callback) { observer = callback; } observe() {} }
    });
    const click = action => events.click({ target: { closest: () => ({ dataset: { notificationAction: action } }) } });
    return { api: window.workLensNotifications, document, sent, Notification, writes, events, click,
        focus() { document.hidden = false; focused = true; events.focus(); },
        changeTitle(title) { document.title = title; observer(); } };
}

test('background completion updates title once and sends only generic content', () => {
    const b = browser();
    b.api.start('a', '摘要');
    assert.equal(b.document.title, '⏳ 摘要處理中｜WorkLens');
    b.api.finish('a', 'success');
    b.api.finish('a', 'success');
    assert.equal(b.document.title, '✓ 摘要已完成｜WorkLens');
    assert.equal(b.sent.length, 1);
    assert.equal(b.sent[0].options.body, '請回到 WorkLens 查看處理結果。');
    b.focus();
    assert.equal(b.document.title, '工作摘要｜WorkLens');
    assert.equal(b.sent[0].closed, true);
});

test('foreground completion stays quiet', () => {
    const b = browser({ hidden: false, focused: true });
    b.api.start('a', '摘要'); b.api.finish('a', 'success');
    assert.equal(b.sent.length, 0);
    assert.equal(b.document.title, '工作摘要｜WorkLens');
});

test('visible but unfocused browser still receives completion', () => {
    const b = browser({ hidden: false });
    b.api.start('a', '摘要'); b.api.finish('a', 'failed');
    assert.equal(b.sent[0].title, '⚠ 摘要失敗');
});

for (const outcome of [null, 'canceled']) test(`${outcome}: preview or cancellation is not completion`, () => {
    const b = browser();
    b.api.start('a', '摘要'); b.api.finish('a', outcome);
    assert.equal(b.sent.length, 0);
    assert.equal(b.document.title, '工作摘要｜WorkLens');
});

test('partial failure remains visible and disposal clears it', () => {
    const b = browser();
    b.api.start('a', '來源回補'); b.api.finish('a', 'partial');
    assert.equal(b.document.title, '⚠ 來源回補部分失敗｜WorkLens');
    b.api.remove('a');
    assert.equal(b.document.title, '工作摘要｜WorkLens');
});

for (const options of [{ permission: 'denied' }, { preference: 'dismissed' }, { supported: false }, { storageFails: true }]) {
    test(`title works without desktop permission: ${JSON.stringify(options)}`, () => {
        const b = browser(options);
        b.api.start('a', '摘要'); b.api.finish('a', 'success');
        assert.equal(b.sent.length, 0);
        assert.equal(b.document.title, '✓ 摘要已完成｜WorkLens');
        assert.equal(b.Notification.requests, 0);
    });
}

test('navigation removes running title and ignores late completion', () => {
    const b = browser();
    b.api.start('a', '摘要'); b.api.remove('a');
    b.changeTitle('通知設定｜WorkLens'); b.api.finish('a', 'success');
    assert.equal(b.document.title, '通知設定｜WorkLens');
    assert.equal(b.sent.length, 0);
});

test('independent operations do not overwrite unseen results; latest base title is restored', () => {
    const b = browser();
    b.api.start('a', '摘要'); b.api.start('b', '來源回補');
    b.changeTitle('工作摘要每週｜WorkLens');
    b.api.finish('a', 'success');
    b.api.finish('b', 'canceled');
    assert.equal(b.document.title, '✓ 摘要已完成｜WorkLens');
    b.focus(); assert.equal(b.document.title, '工作摘要每週｜WorkLens');
});

test('permission is requested synchronously on explicit enable only', async () => {
    const b = browser({ preference: 'unset', permission: 'default' });
    assert.equal(b.Notification.requests, 0);
    const result = b.click('enable');
    assert.equal(b.Notification.requests, 1);
    await result;
    assert.deepEqual(b.writes, ['dismissed']);
});

test('granted permission still requires opt-in; test and disable are explicit', async () => {
    const b = browser({ preference: 'unset' });
    await b.click('enable');
    assert.deepEqual(b.writes, ['enabled']);
    await b.click('test'); assert.equal(b.sent[0].title, 'WorkLens 測試通知');
    await b.click('disable');
    b.api.start('a', '摘要'); b.api.finish('a', 'success');
    assert.equal(b.sent.length, 1);
});

test('preference changes in other tabs take effect', () => {
    const b = browser();
    b.events.storage({ key: 'worklens.notifications.preference', newValue: 'dismissed' });
    b.api.start('a', '摘要'); b.api.finish('a', 'success');
    assert.equal(b.sent.length, 0);
});

function panel(compact = false) {
    const elements = new Map();
    return { isConnected: true, dataset: { notificationCompact: String(compact) }, hidden: compact,
        querySelector(selector) {
            if (!elements.has(selector)) elements.set(selector, {});
            return elements.get(selector);
        } };
}

test('first-use guide hides after dismiss and stays hidden for later operations', async () => {
    const b = browser({ preference: 'unset', permission: 'default' });
    const first = panel(true);
    b.api.attach(first); assert.equal(first.hidden, false);
    await b.click('dismiss'); assert.equal(first.hidden, true);
    const second = panel(true);
    b.api.attach(second); assert.equal(second.hidden, true);
    assert.equal(b.Notification.requests, 0);
});

test('blocked permissions show instructions and disable enable and test', () => {
    const b = browser({ preference: 'unset', permission: 'denied' });
    const settings = panel(); b.api.attach(settings);
    assert.match(settings.querySelector('[data-notification-status]').textContent, /封鎖/);
    assert.equal(settings.querySelector('[data-notification-action="enable"]').disabled, true);
    assert.equal(settings.querySelector('[data-notification-action="test"]').disabled, true);
});

test('revoked permission still lets the user turn off their notification preference', async () => {
    const b = browser({ permission: 'denied' });
    const settings = panel(); b.api.attach(settings);
    assert.equal(settings.querySelector('[data-notification-action="disable"]').hidden, false);
    await b.click('disable');
    assert.equal(settings.querySelector('[data-notification-action="disable"]').hidden, true);
});

test('notification delivery error preserves completed title', () => {
    const b = browser();
    const settings = panel(); b.api.attach(settings);
    b.api.start('a', '摘要'); b.api.finish('a', 'success');
    b.sent[0].onerror();
    assert.equal(b.document.title, '✓ 摘要已完成｜WorkLens');
    assert.match(settings.querySelector('[data-notification-status]').textContent, /未能送出/);
});
