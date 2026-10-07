window.pdfPreview = {
    _currentTask: null,
    _currentUrl: null,

    renderAllPages: async function (containerId, url) {
        if (typeof pdfjsLib === "undefined") {
            console.error("pdfjsLib undefined: legacy build not loaded");
            return;
        }

        pdfjsLib.GlobalWorkerOptions.workerSrc = window.pdfjsWorkerSrc;

        const container = document.getElementById(containerId);

        if (!container) {
            console.warn("PDF container not found:", containerId);
            return;
        }

        // Se è già renderizzato, non rifacciamo tutto
        if (
            this._currentUrl === url &&
            container.dataset.pdfRendered === "true"
        ) {
            return;
        }

        this._currentUrl = url;
        container.dataset.pdfRendered = "false";

        /*
         * MOSTRIAMO SUBITO IL CARICAMENTO.
         *
         * In questo momento il backend potrebbe:
         * - recuperare l'allegato
         * - avviare LibreOffice
         * - convertire XLSX/DOCX in PDF
         */
        container.innerHTML = `
    <div class="document-preview-loader">

        <div class="document-preview-loader-card">

            <div class="document-preview-visual">

                <div class="document-preview-file">
                    <div class="document-preview-file-fold"></div>

                    <div class="document-preview-file-line line-1"></div>
                    <div class="document-preview-file-line line-2"></div>
                    <div class="document-preview-file-line line-3"></div>
                </div>

                <div class="document-preview-processing">
                    <span></span>
                    <span></span>
                    <span></span>
                </div>

            </div>

            <div class="document-preview-title">
                Preparazione anteprima
            </div>

            <div class="document-preview-description">
                Il documento sta per essere visualizzato.
            </div>

            <div class="document-preview-progress">
                <div class="document-preview-progress-value"></div>
            </div>

            <div class="document-preview-caption">
                Potrebbero essere necessari alcuni istanti
            </div>

        </div>

    </div>
`;

        try {
            const loadingTask = pdfjsLib.getDocument({
                url: url,
                disableAutoFetch: false,
                disableStream: false,
                disableRange: false
            });

            this._currentTask = loadingTask;

            /*
             * QUI ASPETTIAMO ANCHE LIBREOFFICE.
             *
             * Infatti /preview non risponde finché
             * il PDF non è stato generato.
             */
            const pdf = await loadingTask.promise;

            if (this._currentUrl !== url) {
                try {
                    await pdf.destroy();
                } catch {
                }

                return;
            }

            container.innerHTML = "";

            const info = document.createElement("div");

            info.className = "pdf-preview-progress";
            info.textContent = `Rendering 0 / ${pdf.numPages}`;

            container.appendChild(info);

            /*
             * Render progressivo:
             * le pagine compaiono una alla volta.
             */
            for (
                let pageNumber = 1;
                pageNumber <= pdf.numPages;
                pageNumber++
            ) {
                if (this._currentUrl !== url) {
                    return;
                }

                const page =
                    await pdf.getPage(pageNumber);

                const viewport =
                    page.getViewport({
                        scale: 1.15
                    });

                const wrapper =
                    document.createElement("div");

                wrapper.className =
                    "pdf-preview-page";

                const canvas =
                    document.createElement("canvas");

                const ctx =
                    canvas.getContext(
                        "2d",
                        {
                            alpha: false
                        }
                    );

                canvas.width =
                    Math.floor(viewport.width);

                canvas.height =
                    Math.floor(viewport.height);

                canvas.style.maxWidth = "100%";
                canvas.style.height = "auto";
                canvas.style.background = "#fff";
                canvas.style.boxShadow =
                    "0 4px 12px rgba(15,23,42,.12)";
                canvas.style.borderRadius =
                    "10px";

                wrapper.appendChild(canvas);
                container.appendChild(wrapper);

                await page.render({
                    canvasContext: ctx,
                    viewport: viewport
                }).promise;

                info.textContent =
                    `Rendering ${pageNumber} / ${pdf.numPages}`;

                await new Promise(resolve =>
                    requestAnimationFrame(resolve)
                );
            }

            container.dataset.pdfRendered =
                "true";

            info.remove();
        }
        catch (error) {
            /*
             * Se nel frattempo abbiamo chiuso/cambiato PDF,
             * evitiamo di mostrare un errore inutile.
             */
            if (this._currentUrl !== url)
                return;

            console.error(
                "Errore rendering PDF:",
                error
            );

            container.innerHTML = `
                <div class="office-preview-error">
                    <strong>Impossibile visualizzare l'anteprima.</strong>
                    <div>
                        Puoi comunque scaricare l'allegato.
                    </div>
                </div>
            `;
        }
        finally {
            if (this._currentUrl === url) {
                this._currentTask = null;
            }
        }
    },

    clear: async function () {
        /*
         * Prima invalidiamo l'URL:
         * eventuali render in corso smetteranno.
         */
        this._currentUrl = null;

        if (this._currentTask) {
            try {
                await this._currentTask.destroy();
            }
            catch (e) {
                console.debug(
                    "PDF task già terminato:",
                    e
                );
            }

            this._currentTask = null;
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