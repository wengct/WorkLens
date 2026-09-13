// Browser-local preferences and attention indicators. No work content leaves the page.
window.workLensNotifications = (() => {
    const key = "worklens.notifications.preference";
    let preference = "unset";
    try { preference = localStorage.getItem(key) || "unset"; } catch { /* Private browsing can deny storage. */ }
    const operations = new Map();
    const panels = new Set();
    const notifications = new Map();
    let originalTitle = document.title;
    let appliedTitle = null;
    let permissionPending = false;
    let feedback = "";
    const supported = () => window.isSecureContext && "Notification" in window;
    const foreground = () => !document.hidden && document.hasFocus();
    const permitted = () => supported() && Notification.permission === "granted" && preference === "enabled";

    function save(value) {
        preference = value;
        if (value !== "enabled") for (const id of notifications.keys()) closeNotification(id);
        try { localStorage.setItem(key, value); } catch { feedback = "瀏覽器無法保存設定，本次開啟期間仍有效。"; }
        refreshPanels();
    }

    function updateTitle() {
        const values = [...operations.values()];
        const pending = values.filter(item => item.result).at(-1);
        const running = values.filter(item => item.active).at(-1);
        const item = pending || running;
        const title = item ? `${item.result || `⏳ ${item.label}處理中`}｜WorkLens` : originalTitle;
        appliedTitle = item ? title : null;
        if (document.title !== title) document.title = title;
    }

    // PageTitle can update independently of the interactive page's circuit.
    new MutationObserver(() => {
        if (document.title !== appliedTitle) {
            originalTitle = document.title;
            if (operations.size) updateTitle();
        }
    }).observe(document.head, { childList: true, subtree: true, characterData: true });

    function closeNotification(id) {
        notifications.get(id)?.close();
        notifications.delete(id);
    }

    function acknowledge() {
        if (!foreground()) return;
        for (const [id, item] of operations) {
            if (!item.active) operations.delete(id);
        }
        for (const id of notifications.keys()) closeNotification(id);
        updateTitle();
        refreshPanels();
    }

    function notify(id, title) {
        try {
            closeNotification(id);
            const notification = new Notification(title, {
                body: "請回到 WorkLens 查看處理結果。", tag: `worklens-${id}`, icon: "/favicon-32x32.png"
            });
            notifications.set(id, notification);
            notification.onclick = () => { window.focus(); closeNotification(id); acknowledge(); };
            notification.onclose = () => { if (notifications.get(id) === notification) notifications.delete(id); };
            notification.onerror = () => { feedback = "通知未能送出，請檢查瀏覽器與系統通知設定。"; refreshPanels(); };
            return true;
        } catch {
            feedback = "通知未能送出，請檢查瀏覽器與系統通知設定。";
            refreshPanels();
            return false;
        }
    }

    function refreshPanels() {
        for (const panel of panels) {
            if (!panel.isConnected) { panels.delete(panel); continue; }
            const compact = panel.dataset.notificationCompact === "true";
            const permission = supported() ? Notification.permission : "unsupported";
            panel.hidden = compact && (preference !== "unset" || permission !== "default");
            panel.querySelector("[data-notification-status]").textContent = feedback ||
                (permission === "unsupported" ? "此瀏覽器或連線環境不支援桌面通知，仍可使用分頁標題提醒。" :
                permission === "denied" ? "通知已被瀏覽器封鎖。請在網址列的網站設定允許通知，再回來開啟。" :
                permitted() ? "桌面完成通知已開啟。若未收到，請檢查系統通知與勿擾設定。" : "桌面完成通知尚未開啟。");
            const enable = panel.querySelector('[data-notification-action="enable"]');
            enable.hidden = permitted();
            enable.disabled = permissionPending || permission === "denied" || permission === "unsupported";
            panel.querySelector('[data-notification-action="disable"]').hidden = preference !== "enabled";
            panel.querySelector('[data-notification-action="test"]').disabled = !permitted();
        }
    }

    // Request permission in the original browser click, not after a Blazor Server round trip.
    document.addEventListener("click", async event => {
        const button = event.target.closest?.("[data-notification-action]");
        if (!button || button.disabled) return;
        const action = button.dataset.notificationAction;
        feedback = "";
        if (action === "enable") {
            if (!supported() || permissionPending || Notification.permission === "denied") return;
            permissionPending = true;
            refreshPanels();
            try {
                const permission = Notification.permission === "granted" ? "granted" : await Notification.requestPermission();
                save(permission === "granted" ? "enabled" : "dismissed");
            } catch { save("dismissed"); feedback = "無法開啟通知，請檢查瀏覽器的網站設定。"; }
            finally { permissionPending = false; refreshPanels(); }
        } else if (action === "disable" || action === "dismiss") save("dismissed");
        else if (action === "test" && permitted()) {
            if (notify("test", "WorkLens 測試通知")) feedback = "已送出測試通知。若未看到，請檢查系統通知與勿擾設定。";
            refreshPanels();
        }
    });
    window.addEventListener("focus", acknowledge);
    document.addEventListener("visibilitychange", acknowledge);
    window.addEventListener("storage", event => {
        if (event.key === key) { preference = event.newValue || "unset"; feedback = ""; refreshPanels(); }
    });

    return {
        attach(panel) { panels.add(panel); refreshPanels(); },
        detach(panel) { panels.delete(panel); },
        start(id, label) {
            if (!operations.size) originalTitle = document.title;
            closeNotification(id);
            operations.delete(id);
            operations.set(id, { label, active: true });
            updateTitle();
        },
        finish(id, outcome) {
            const item = operations.get(id);
            if (!item?.active) return;
            item.active = false;
            if (!outcome || outcome === "canceled" || foreground()) operations.delete(id);
            else {
                const suffix = outcome === "success" ? "已完成" : outcome === "partial" ? "部分失敗" : "失敗";
                item.result = `${outcome === "success" ? "✓" : "⚠"} ${item.label}${suffix}`;
                if (permitted()) notify(id, item.result);
            }
            updateTitle();
        },
        remove(id) { operations.delete(id); closeNotification(id); updateTitle(); }
    };
})();
