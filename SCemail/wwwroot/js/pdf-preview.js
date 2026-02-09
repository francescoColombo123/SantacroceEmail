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
