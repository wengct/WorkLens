(function () {
    const editors = new Map();

    window.workLensEasyMde = {
        initialize: function (elementId) {
            const element = document.getElementById(elementId);
            if (!element) {
                throw new Error(`找不到 Markdown 編輯器元素：${elementId}`);
            }

            if (typeof window.EasyMDE !== "function") {
                throw new Error("EasyMDE 離線資產載入失敗。");
            }

            this.dispose(elementId);
            const editor = new window.EasyMDE({
                element: element,
                autoDownloadFontAwesome: false,
                autofocus: false,
                forceSync: true,
                nativeSpellcheck: true,
                spellChecker: false,
                uploadImage: false,
                toolbarGuideIcon: false,
                status: false,
                minHeight: "260px",
                placeholder: "使用 Markdown 記錄今天做過的事…",
                toolbar: [
                    "bold", "italic", "heading", "|",
                    "unordered-list", "ordered-list", "quote", "code", "link", "|",
                    "preview", "side-by-side", "fullscreen"
                ]
            });
            editors.set(elementId, editor);
        },

        getValue: function (elementId) {
            const editor = editors.get(elementId);
            if (!editor) {
                const element = document.getElementById(elementId);
                return element ? element.value : "";
            }

            return editor.value();
        },

        setValue: function (elementId, value) {
            const editor = editors.get(elementId);
            if (editor) {
                editor.value(value || "");
                return;
            }

            const element = document.getElementById(elementId);
            if (element) {
                element.value = value || "";
            }
        },

        dispose: function (elementId) {
            const editor = editors.get(elementId);
            if (!editor) {
                return;
            }

            editor.toTextArea();
            editors.delete(elementId);
        }
    };
})();
