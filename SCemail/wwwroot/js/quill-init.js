window.destroyQuill = (editorId) => {
    const current = document.getElementById(editorId);
    if (!current) return;

    const parent = current.parentNode;
    if (!parent) return;

    const q = current.__quill;
    const handler = current.__quillHandler;

    if (q) {
        try { if (handler) q.off("text-change", handler); } catch (e) { }
        try { q.disable(); } catch (e) { }
    }

    parent.querySelectorAll(".ql-toolbar").forEach(n => n.remove());
    parent.querySelectorAll(".ql-container").forEach(n => n.remove());

    parent.querySelectorAll(`#${editorId}`).forEach(n => n.remove());

    const fresh = document.createElement("div");
    fresh.id = editorId;
    parent.appendChild(fresh);
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

    let s = (quoteHtmlOrText || "")
        .replace(/\r\n/g, "\n")
        .replace(/\r/g, "\n")
        .trim();

    const looksHtml = /<\/?\w+[^>]*>/i.test(s) || /&lt;\/?\w+/i.test(s);

    if (!looksHtml) {
        s = encodeHtml(s).replace(/\n/g, "<br>");
    } else {
        s = s
            .replace(/\n{2,}/g, "<br><br>")
            .replace(/\n/g, "<br>");
    }

    s = s.replace(/(<br\s*\/?>\s*){3,}/gi, "<br><br>");

    return s;
};
window.initQuill = (editorId, dotnetRef, bodyHtml, signatureText, quoteHtml) => {
    window.destroyQuill(editorId);

    const host = document.getElementById(editorId);
    if (!host) return;

    const quill = new Quill(host, {
        theme: "snow",
        modules: {
            toolbar: [
                ["bold", "italic", "underline"],
                [{ list: "ordered" }, { list: "bullet" }],
                ["link"],
                ["clean"]
            ]
        }
    });

    host.__quill = quill;

    const body = (bodyHtml || "").trim();
    const firma = (signatureText || "").trim();
    const quote = (quoteHtml || "").trim();

    if (body && body !== "<p><br></p>") {
        quill.clipboard.dangerouslyPasteHTML(0, body, "api");
    } else {
        quill.setText("\n", "api");
    }

    let index = quill.getLength() - 1;

    if (firma) {
        const firmaHtml = firma
            .replace(/\r\n/g, "\n")
            .replace(/\r/g, "\n")
            .split("\n")
            .map(riga => riga.trim())
            .map(riga => riga === "" ? "<br>" : riga)
            .join("<br>");

        quill.clipboard.dangerouslyPasteHTML(
            index,
            "<div style='margin-top:12px; line-height:1.5;'>" + firmaHtml + "</div><br>",
            "api"
        );

        index = quill.getLength() - 1;
    }
    if (quote) {
        quill.insertText(index, "\n----------------------------------------------------------------------\n\n", "api");
        index = quill.getLength() - 1;

        quill.clipboard.dangerouslyPasteHTML(
            index,
            "<blockquote>" + quote + "</blockquote>",
            "api"
        );
    }

    quill.setSelection(0, 0, "api");

    const handler = () => {
        try {
            dotnetRef.invokeMethodAsync("UpdateBodyHtml", quill.root.innerHTML || "");
        } catch { }
    };

    host.__quillHandler = handler;
    quill.on("text-change", handler);
    handler();

    setTimeout(() => quill.focus(), 0);
};
window.registerComposeAutoSaveClose = function (dotnetRef) {
    if (window.__composeAutoSaveRegistered)
        return;

    window.__composeAutoSaveRegistered = true;

    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape") {
            dotnetRef.invokeMethodAsync("CloseFromJs");
        }
    });

    document.addEventListener("mousedown", function (e) {
        const dialog = document.querySelector(".mud-dialog");

        if (dialog && !dialog.contains(e.target)) {
            dotnetRef.invokeMethodAsync("CloseFromJs");
        }
    });
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