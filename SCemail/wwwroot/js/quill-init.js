// ✅ Inizializza Quill
window.initQuill = (elementId, dotnetRef, initialContent) => {
    console.log("✅ Inizializzo Quill per", elementId);

    const quill = new Quill(`#${elementId}`, {
        theme: 'snow',
        placeholder: 'Scrivi il tuo messaggio...',
        modules: {
            toolbar: [
                ['bold', 'italic', 'underline', 'strike'],
                [{ 'list': 'ordered' }, { 'list': 'bullet' }],
                ['link', 'image'],
                [{ 'align': [] }],
                ['clean']
            ]
        }
    });

    // Imposta il contenuto iniziale
    if (initialContent) {
        quill.root.innerHTML = initialContent;
    }

    // 🔥 Forza font identico alla schermata che mi hai inviato
    quill.root.style.fontFamily = "'Segoe UI', Roboto, Arial, Helvetica, sans-serif";
    quill.root.style.fontSize = "15px";
    quill.root.style.lineHeight = "1.6";

    // Aggiorna Blazor quando cambia il testo
    quill.on('text-change', function () {
        dotnetRef.invokeMethodAsync('UpdateBodyHtml', quill.root.innerHTML);
    });
};



// ✅ Inizializza zona di drop per allegati
window.initFileDropZone = (element) => {
    if (!element) return;

    element.addEventListener('dragover', (e) => {
        e.preventDefault();
        element.style.background = '#eaf2ff';
    });

    element.addEventListener('dragleave', (e) => {
        e.preventDefault();
        element.style.background = '#f9fbff';
    });

    element.addEventListener('drop', (e) => {
        e.preventDefault();
        element.style.background = '#f9fbff';

        const input = element.querySelector('input[type=file]');
        if (input && e.dataTransfer?.files?.length > 0) {
            input.files = e.dataTransfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }
    });
};
