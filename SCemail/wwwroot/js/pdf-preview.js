window.pdfPreview = {
    renderAllPages: async function (containerId, url) {
        if (typeof pdfjsLib === "undefined") {
            console.error("pdfjsLib undefined: legacy build not loaded");
            return;
        }

        pdfjsLib.GlobalWorkerOptions.workerSrc = window.pdfjsWorkerSrc;

        const container = document.getElementById(containerId);
        if (!container) return;

        container.innerHTML = "";

        const pdf = await pdfjsLib.getDocument(url).promise;

        for (let pageNumber = 1; pageNumber <= pdf.numPages; pageNumber++) {
            const page = await pdf.getPage(pageNumber);

            const viewport = page.getViewport({ scale: 1.4 });

            const canvas = document.createElement("canvas");
            const ctx = canvas.getContext("2d");

            canvas.width = viewport.width;
            canvas.height = viewport.height;

            canvas.style.maxWidth = "100%";
            canvas.style.height = "auto";
            canvas.style.background = "#fff";
            canvas.style.boxShadow = "0 4px 12px rgba(15,23,42,.12)";
            canvas.style.borderRadius = "10px";

            container.appendChild(canvas);

            await page.render({
                canvasContext: ctx,
                viewport: viewport
            }).promise;
        }
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