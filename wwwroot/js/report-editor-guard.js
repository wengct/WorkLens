window.workLensReportEditor = (() => {
    const editors = new Map();

    const hasDirtyEditor = () => Array.from(editors.values()).some(editor => editor.dirty);
    window.addEventListener("beforeunload", event => {
        if (!hasDirtyEditor()) return;
        event.preventDefault();
        event.returnValue = "";
    });

    return {
        attach(id) {
            const element = document.getElementById(id);
            if (!element || editors.has(id)) return;
            const input = () => {
                const editor = editors.get(id);
                if (editor) editor.dirty = true;
            };
            element.addEventListener("input", input);
            editors.set(id, { element, input, dirty: false });
        },
        markSaved(id) {
            const editor = editors.get(id);
            if (editor) editor.dirty = false;
        },
        detach(id) {
            const editor = editors.get(id);
            if (!editor) return;
            editor.element.removeEventListener("input", editor.input);
            editors.delete(id);
        }
    };
})();
