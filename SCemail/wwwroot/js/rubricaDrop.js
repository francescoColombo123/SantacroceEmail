window.rubricaDrop = (() => {
    async function fileToBase64(file) {
        const buf = await file.arrayBuffer();
        const bytes = new Uint8Array(buf);

        let binary = "";
        const chunkSize = 0x8000;
        for (let i = 0; i < bytes.length; i += chunkSize) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
        }
        return btoa(binary);
    }

    function init(dropZoneId, dotNetRef) {
        const dz = document.getElementById(dropZoneId);
        if (!dz) return;

        // Evita che Firefox apra/scarichi il file (serve capture)
        const prevent = (ev) => {
            if (!dz.contains(ev.target)) return;
            ev.preventDefault();
            // NON stopPropagation -> altrimenti rompi eventi Blazor
        };

        window.addEventListener("dragover", prevent, true);
        window.addEventListener("drop", prevent, true);

        // Handler drop nativo sul dropzone
        dz.addEventListener("drop", async (ev) => {
            ev.preventDefault();

            const files = ev.dataTransfer?.files;
            if (!files || files.length === 0) return;

            const file = files[0];
            const base64 = await fileToBase64(file);

            // chiama .NET (istanza componente)
            await dotNetRef.invokeMethodAsync("OnFileDropped", file.name, base64);
        });
    }

    function openPicker(inputId) {
        const input = document.getElementById(inputId);
        if (input) input.click();
    }

    function clearInput(inputId) {
        const input = document.getElementById(inputId);
        if (input) input.value = ""; // permette di riselezionare lo stesso file
    }

    return { init, openPicker, clearInput };
})();
