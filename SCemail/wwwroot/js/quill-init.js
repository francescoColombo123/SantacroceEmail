window.destroyQuill = (editorId) => {
    const el = document.getElementById(editorId);
    if (!el) return;

    const q = el.__quill;
    const handler = el.__quillHandler;

    if (q) {
        try { if (handler) q.off("text-change", handler); } catch (e) { }
        try { q.disable(); } catch (e) { }
    }

    // ✅ rimuovi toolbar + container che Quill ha creato (evita doppie toolbar)
    const parent = el.parentNode;
    if (parent) {
        // Se #editor è già diventato .ql-container, la toolbar è tipicamente sibling
        parent.querySelectorAll(".ql-toolbar").forEach(n => n.remove());
        parent.querySelectorAll(".ql-container").forEach(n => n.remove());

        // ricrea un editor pulito
        const fresh = document.createElement("div");
        fresh.id = editorId;
        fresh.style.cssText = el.style.cssText;
        parent.appendChild(fresh);
    }

    try { el.__quill = null; } catch (e) { }
    try { el.__quillHandler = null; } catch (e) { }
};

window.initFileDropZone = (element) => {
    if (!element) return;

    // evita doppia registrazione se init chiamato più volte
    if (element.__dropzoneInit) return;
    element.__dropzoneInit = true;

    element.addEventListener("dragover", (e) => {
        e.preventDefault();
        element.style.background = "#eaf2ff";
    });

    element.addEventListener("dragleave", (e) => {
        e.preventDefault();
        element.style.background = "#f9fbff";
    });

    element.addEventListener("drop", (e) => {
        e.preventDefault();
        element.style.background = "#f9fbff";

        const input = element.querySelector('input[type="file"]');
        if (input && e.dataTransfer?.files?.length > 0) {
            input.files = e.dataTransfer.files;
            input.dispatchEvent(new Event("change", { bubbles: true }));
        }
    });
};
const encodeHtml = (s) =>
    (s ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;");

const normalizeQuoteHtml = (quoteHtmlOrText) => {
    if (!quoteHtmlOrText) return "";

    let s = (quoteHtmlOrText || "").replace(/\r\n/g, "\n").trim();

    // se NON sembra html → encoda e metti <br>
    const looksHtml = /<\/?\w+[^>]*>/i.test(s) || /&lt;\/?\w+/i.test(s);
    if (!looksHtml) {
        s = encodeHtml(s).replace(/\n/g, "<br>");
    } else {
        // se è escaped tipo &lt;div&gt; → lascialo com’è (tu già fai HtmlDecode in C#)
        // qui facciamo solo una normalizzazione soft di newline → <br> se mancano
        // (non tocchiamo troppo per non rompere tag)
    }

    // ✅ forza a capo “Gmail-like” per header comuni (IT/EN)
    // Inserisce <br> PRIMA delle etichette se sono attaccate
    const labels = [
        "Da:", "A:", "Cc:", "CC:", "Ccn:", "CCN:",
        "Oggetto:", "Inviato:", "Data:", "Date:", "Subject:", "To:", "From:"
    ];

    for (const lab of labels) {
        // aggiunge <br> prima dell'etichetta se non è già a inizio riga o preceduta da <br>
        const re = new RegExp(`(?!^)(?<!<br>)(?<!\\n)\\s*(${lab.replace(":", "\\:")})`, "g");
        s = s.replace(re, "<br>$1");
    }

    // un minimo di respiro all’inizio
    if (!s.startsWith("<br>")) s = "<br>" + s;

    return s;
};

window.initQuill = (editorId, dotnetRef, bodyHtml, quoteHtml) => {
    window.destroyQuill(editorId);

    const host = document.getElementById(editorId);
    if (!host) return;

    const toolbarOptions = [
        ["bold", "italic", "underline"],
        [{ list: "ordered" }, { list: "bullet" }],
        ["link"],
        ["clean"],
    ];

    const quill = new Quill(host, {
        theme: "snow",
        modules: { toolbar: toolbarOptions },
    });

    host.__quill = quill;

    const body = (bodyHtml || "").trim();
    const quote = (quoteHtml || "").trim();

    // 1) BODY
    quill.clipboard.dangerouslyPasteHTML(0, body ? body : "<p><br></p>", "api");

    // 2) QUOTE (se presente) con separatore + blockquote
    if (quote) {
        const sepIndex = quill.getLength() - 1;

        const normalized = normalizeQuoteHtml(quote);

        // separatore + marker per ritrovarlo dopo
        const html = `
          <p><br></p>
          <p>----------------------------------------------------------------------</p>
          <p><br></p>
          <blockquote>
            <div>${normalized}</div>
          </blockquote>
        `;


        quill.clipboard.dangerouslyPasteHTML(sepIndex, html, "api");

        // cursore: subito prima del separatore
        quill.setSelection(sepIndex, 0, "api");

        setTimeout(() => quill.focus(), 0);
    } else {
        quill.setSelection(0, 0, "api");
        setTimeout(() => quill.focus(), 0);
    }

    // 3) sync verso C# (INVIO/BOZZA)
    // (per ora manda tutto; se vuoi solo body, vedi nota più sotto)
    const handler = () => {
        try {
            dotnetRef.invokeMethodAsync("UpdateBodyHtml", (quill.root.innerHTML || "").trim());
        } catch { }
    };

    host.__quillHandler = handler;
    quill.on("text-change", handler);
    handler();
};

window.initQuillTask = (editorId, toolbarId, dotnetRef, initialHtml) => {
    const editorEl = document.getElementById(editorId);
    const toolbarEl = document.getElementById(toolbarId);

    if (!editorEl || !toolbarEl) return;

    // toolbar HTML (puoi personalizzarla)
    toolbarEl.innerHTML = `
    <span class="ql-formats">
      <button class="ql-bold"></button>
      <button class="ql-italic"></button>
      <button class="ql-underline"></button>
    </span>
    <span class="ql-formats">
      <button class="ql-list" value="ordered"></button>
      <button class="ql-list" value="bullet"></button>
    </span>
    <span class="ql-formats">
      <button class="ql-link"></button>
    </span>
  `;

    const quill = new Quill(editorEl, {
        theme: "snow",
        modules: { toolbar: toolbarEl }
    });

    if (initialHtml) quill.clipboard.dangerouslyPasteHTML(initialHtml);

    quill.on("text-change", () => {
        const html = editorEl.querySelector(".ql-editor")?.innerHTML ?? "";
        dotnetRef?.invokeMethodAsync("UpdateTaskHtml", html);
    });

    // opzionale: salva istanza per cleanup
    editorEl.__quill = quill;
};