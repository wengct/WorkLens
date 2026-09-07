(function () {
    const forms = new Map();
    const snapshots = new Map();
    let nextSnapshot = 0;
    const empty = { date: "", hours: "", projectId: "", title: "", content: "" };
    function read(state) {
        const input = { ...empty };
        state.root.querySelectorAll('[data-draft-field]').forEach(element => {
            input[element.dataset.draftField] = element.dataset.draftField === 'content'
                ? window.workLensEasyMde.getValue(element.id) : element.value;
        });
        return input;
    }
    function changed(state) {
        if (state.replacing) return;
        state.revision++;
        state.dirty = true;
        state.status.textContent = '暫存中…';
        clearTimeout(state.timer);
        state.timer = setTimeout(() => flush(state), 1000);
    }
    function capture(state) {
        const text = JSON.stringify(read(state));
        const id = String(++nextSnapshot);
        snapshots.set(id, text);
        return { id, length: text.length };
    }
    async function flush(state) {
        clearTimeout(state.timer);
        if (state.pending) await state.pending;
        if (!state.dirty) return true;
        const revision = state.revision;
        const snapshot = capture(state);
        state.pending = state.dotnet.invokeMethodAsync('SaveSnapshotAsync', snapshot)
            .then(ok => {
                if (ok && revision === state.revision) state.dirty = false;
                state.status.textContent = ok ? (state.dirty ? '暫存中…' : '草稿已暫存') : '草稿暫存失敗';
                return ok;
            }).catch(() => {
                state.status.textContent = '草稿暫存失敗；請恢復連線後重試。';
                return false;
            }).finally(() => snapshots.delete(snapshot.id));
        const result = await state.pending;
        state.pending = null;
        return result && (!state.dirty || await flush(state));
    }
    window.addEventListener('beforeunload', event => {
        if ([...forms.values()].some(state => state.dirty || state.pending)) {
            event.preventDefault();
            event.returnValue = '';
        }
    });
    window.workLensDrafts = {
        attach(id, dotnet, navigateDates, status) {
            const root = document.getElementById(id);
            const state = { root, dotnet, status: root.querySelector('[data-draft-status]'), revision: 0, dirty: false };
            state.status.textContent = status;
            const content = root.querySelector('[data-draft-field="content"]');
            try { window.workLensEasyMde.initialize(content.id, '190px'); }
            catch { state.status.textContent = 'Markdown 編輯器無法載入，已切換為一般文字輸入。'; }
            state.change = () => changed(state);
            const date = root.querySelector('[data-draft-field="date"]');
            state.date = date.value;
            state.dateChange = event => {
                if (!navigateDates || event.target !== date || !date.value || date.value === state.date) return;
                const next = date.value;
                date.value = state.date;
                dotnet.invokeMethodAsync('ChangeDateAsync', next).catch(() => {
                    state.status.textContent = '切換日期失敗，請保留目前輸入後重試。';
                });
            };
            root.addEventListener('input', state.change);
            root.addEventListener('change', state.change);
            root.addEventListener('change', state.dateChange);
            window.workLensEasyMde.onChange(content.id, state.change);
            forms.set(id, state);
        },
        capture(id) { return capture(forms.get(id)); },
        // Even escaped Unicode chunks remain below SignalR's default message limit.
        readChunk(id, offset) {
            const text = snapshots.get(id);
            let end = Math.min(offset + 4000, text.length);
            const last = text.charCodeAt(end - 1);
            if (end < text.length && last >= 0xD800 && last <= 0xDBFF) end--;
            return text.slice(offset, end);
        },
        releaseSnapshot(id) { snapshots.delete(id); },
        flush(id) { return flush(forms.get(id)); },
        async flushAll() {
            for (const state of forms.values()) if (!await flush(state)) return false;
            return true;
        },
        hasInput(id) {
            const input = read(forms.get(id));
            return !!(input.hours || input.title || input.content);
        },
        committed(id) {
            const state = forms.get(id);
            clearTimeout(state.timer);
            state.dirty = false;
            state.status.textContent = '已儲存';
        },
        setStatus(id, text) { forms.get(id).status.textContent = text; },
        async abandon(id) {
            const state = forms.get(id);
            clearTimeout(state.timer);
            if (state.pending) await state.pending;
            clearTimeout(state.timer);
            state.dirty = false;
        },
        replace(id, input, dirty) {
            const state = forms.get(id);
            state.replacing = true;
            state.root.querySelectorAll('[data-draft-field]').forEach(element => {
                const value = input[element.dataset.draftField] || '';
                if (element.dataset.draftField === 'content') window.workLensEasyMde.setValue(element.id, value);
                else element.value = value;
            });
            state.replacing = false;
            state.dirty = false;
            if (dirty) changed(state);
        },
        focusHours(id) { forms.get(id)?.root.querySelector('[data-draft-field="hours"]')?.focus(); },
        setBusy(id, value) {
            const state = forms.get(id);
            if (!state) return;
            state.root.querySelector('fieldset').disabled = value;
            window.workLensEasyMde.setReadOnly(state.root.querySelector('[data-draft-field="content"]').id, value);
        },
        detach(id) {
            const state = forms.get(id);
            if (!state) return;
            clearTimeout(state.timer);
            state.root.removeEventListener('input', state.change);
            state.root.removeEventListener('change', state.change);
            state.root.removeEventListener('change', state.dateChange);
            window.workLensEasyMde.dispose(state.root.querySelector('[data-draft-field="content"]').id);
            forms.delete(id);
        }
    };
})();
