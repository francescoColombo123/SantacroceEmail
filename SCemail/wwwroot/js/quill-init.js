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

    // pulizia html inutile
    s = s.replace(/<\/?(html|body)[^>]*>/gi, "");
    s = s.replace(/<style[\s\S]*?<\/style>/gi, "");
    s = s.replace(/<script[\s\S]*?<\/script>/gi, "");
    s = s.replace(/<!--[\s\S]*?-->/g, "");

    // elimina quote vecchie annidate
    s = s.replace(
        /<blockquote[^>]*data-sc-quote=["']1["'][^>]*>[\s\S]*?<\/blockquote>/gi,
        ""
    );

    // converte div/p in br
    s = s.replace(/<\/div>/gi, "<br>");
    s = s.replace(/<\/p>/gi, "<br>");
    s = s.replace(/<div[^>]*>/gi, "");
    s = s.replace(/<p[^>]*>/gi, "");

    // elimina spazi enormi
    s = s.replace(/[ \t]{2,}/g, " ");

    // elimina righe vuote infinite
    s = s.replace(/\n{3,}/g, "\n\n");

    // elimina <br> multipli
    s = s.replace(/(<br\s*\/?>\s*){3,}/gi, "<br><br>");

    // se NON è html => encode
    const looksHtml = /<\/?\w+[^>]*>/i.test(s);

    if (!looksHtml) {
        s = encodeHtml(s)
            .replace(/\n\n/g, "<br><br>")
            .replace(/\n/g, "<br>");
    }

    return s.trim();
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
    host.__dotnetRef = dotnetRef;
    const body = (bodyHtml || "").trim();
    const firma = normalizeSignatureHtml(signatureText || "");
    const quote = (quoteHtml || "").trim();

    if (body && body !== "<p><br></p>") {
        quill.clipboard.dangerouslyPasteHTML(0, body, "api");
    } else {
        quill.setText("\n", "api");
    }

    let index = quill.getLength() - 1;

    if (firma) {
        quill.clipboard.dangerouslyPasteHTML(
            index,
            `<br><div data-sc-signature="1" style="margin-top:12px; line-height:1.5;">${firma}</div><br>`,
            "api"
        );
        index = quill.getLength() - 1;
    }

    if (quote) {
        const cleanQuote = normalizeQuoteHtml(quote);

        quill.clipboard.dangerouslyPasteHTML(
            index,
            `
        <div data-sc-quote-wrapper="1" style="margin-top:18px;">
            <hr style="border:none;border-top:1px solid #cfcfcf;margin:14px 0;">
            <div style="font-size:13px;color:#666;margin-bottom:8px;">
                Messaggio precedente:
            </div>
            <blockquote data-sc-quote="1"
                style="
                    margin:0 0 0 8px;
                    padding-left:12px;
                    border-left:2px solid #ccc;
                    color:#222;
                    font-family:'Courier New',monospace;
                    font-size:14px;
                    line-height:1.4;
                    white-space:normal;
                    word-break:break-word;
                ">
                ${cleanQuote}
            </blockquote>
        </div>
        `,
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
function normalizeSignatureHtml(signatureHtml) {
    let s = signatureHtml || "";

    s = s.replace(/\r\n/g, "\n").replace(/\r/g, "\n");

    if (!/<br\s*\/?>|<\/div>|<\/p>/i.test(s)) {
        s = s.replace(/\n/g, "<br>");
    }

    s = s.replace(/\s*(\d{8,15})\s*/g, "<br>$1<br><br>");
    s = s.replace(/\s*(EUROCEREALI SRL|GRUPPO SANTACROCE)\s*/gi, "<br><br>$1");

    s = s.replace(/(<br\s*\/?>\s*){3,}/gi, "<br><br>");

    return s.trim();
}
window.composeInterop = window.composeInterop || {};

window.composeInterop.replaceSignature = function (editorId, signatureHtml) {
    const host = document.getElementById(editorId);
    const quill = host?.__quill;
    const dotnetRef = host?.__dotnetRef;

    if (!quill) return;

    const root = quill.root;

    // 1) prendo il blocco quote vero dal DOM, non con regex
    const quoteNode = root.querySelector('[data-sc-quote-wrapper="1"]');
    const quoteHtml = quoteNode ? quoteNode.outerHTML : "";

    // 2) rimuovo temporaneamente la quote
    if (quoteNode) {
        quoteNode.remove();
    }

    // 3) rimuovo tutte le firme marcate
    root.querySelectorAll('[data-sc-signature="1"]').forEach(n => n.remove());

    // 4) fallback: rimuovo firme vecchie non marcate SOLO dal contenuto rimasto
    let html = root.innerHTML || "";

    html = html.replace(
        /(<br\s*\/?>\s*)*Cordiali saluti\.[\s\S]*?(EUROCEREALI SRL|GRUPPO SANTACROCE)(<\/[^>]+>|<br\s*\/?>|\s)*/gi,
        ""
    );

    html = html.replace(/(<br\s*\/?>\s*){3,}/gi, "<br><br>").trim();

    const normalizedSignature = normalizeSignatureHtml(signatureHtml || "");

    // 5) ricostruisco SEMPRE: testo utente + firma + quote
    let newHtml = html;

    if (normalizedSignature) {
        newHtml += `<br><div data-sc-signature="1" style="margin-top:12px; line-height:1.5;">${normalizedSignature}</div><br>`;
    }

    if (quoteHtml) {
        newHtml += `<br>${quoteHtml}`;
    }

    quill.setText("", "api");
    quill.clipboard.dangerouslyPasteHTML(0, newHtml, "api");

    if (dotnetRef) {
        dotnetRef.invokeMethodAsync("UpdateBodyHtml", quill.root.innerHTML || "");
    }
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