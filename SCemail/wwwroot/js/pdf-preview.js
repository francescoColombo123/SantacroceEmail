window.pdfPreview = {
    render: async function (canvasId, url) {
        // qui ora DEVE esistere
        if (typeof pdfjsLib === "undefined") {
            console.error("pdfjsLib undefined: legacy build not loaded");
            return;
        }

        pdfjsLib.GlobalWorkerOptions.workerSrc = window.pdfjsWorkerSrc;

        const canvas = document.getElementById(canvasId);
        if (!canvas) return;

        const pdf = await pdfjsLib.getDocument(url).promise;
        const page = await pdf.getPage(1);

        const viewport = page.getViewport({ scale: 1.4 });
        const ctx = canvas.getContext("2d");

        canvas.width = viewport.width;
        canvas.height = viewport.height;

        await page.render({ canvasContext: ctx, viewport }).promise;
    }
};
window.attachmentViewer = {
    _handler: null,

    registerEscape: function (dotNetRef) {
        this.unregisterEscape();

        this._handler = function (e) {
            if (e.key === "Escape") {
                e.preventDefault();
                dotNetRef.invokeMethodAsync("OnEscapePressed");
            }
        };

        window.addEventListener("keydown", this._handler);
    },

    unregisterEscape: function () {
        if (this._handler) {
            window.removeEventListener("keydown", this._handler);
            this._handler = null;
        }
    },

    focusElement: function (el) {
        if (el) {
            el.focus();
        }
    }
};