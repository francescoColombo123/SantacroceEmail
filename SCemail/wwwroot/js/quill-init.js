window.destroyQuill = (editorId) => {
    const current = document.getElementById(editorId);
    if (!current) return;

    const shell = current.closest(".compose-editor-shell");
    if (!shell) return;

    const q = current.__quill;
    const handler = current.__quillHandler;

    if (q) {
        try {
            if (handler)
                q.off("text-change", handler);
        } catch { }

        try {
            q.disable();
        } catch { }
    }

    shell.querySelectorAll(".ql-container").forEach(n => n.remove());

    let editor = shell.querySelector(`#${editorId}`);

    if (!editor) {
        editor = document.createElement("div");
        editor.id = editorId;
    }

    const toolbar = shell.querySelector("#composeToolbar");

    if (toolbar) {
        toolbar.insertAdjacentElement("afterend", editor);
    } else {
        shell.prepend(editor);
    }

    editor.innerHTML = "";

    editor.__quill = null;
    editor.__quillHandler = null;
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
const normalizeQuoteHtml = (quoteHtmlOrText) => {
    if (!quoteHtmlOrText) return "";

    let s = (quoteHtmlOrText || "").trim();

    s = s.replace(/<script[\s\S]*?<\/script>/gi, "");
    s = s.replace(/<style[\s\S]*?<\/style>/gi, "");
    s = s.replace(/<!--[\s\S]*?-->/g, "");

    s = s.replace(/(<br\s*\/?>\s*){4,}/gi, "<br><br>");
    s = s.replace(/^(\s|<br\s*\/?>|<p><br><\/p>|&nbsp;)+/gi, "");
    s = s.replace(/(\s|<br\s*\/?>|<p><br><\/p>|&nbsp;)+$/gi, "");

    return s.trim();
};

window.initQuill = (editorId, dotnetRef, bodyHtml, signatureText, quoteHtml) => {
    window.destroyQuill(editorId);

    const host = document.getElementById(editorId);
    if (!host) return;

    const quill = new Quill(host, {
        theme: "snow",
        modules: {
            toolbar: document.getElementById("composeToolbar")
        }
    });

    host.__quill = quill;
    host.__dotnetRef = dotnetRef;

    const body = (bodyHtml || "").trim();
    const firma = normalizeSignatureHtml(signatureText || "");
    const quote = (quoteHtml || "").trim();
    let html = body && body !== "<p><br></p>" ? body : "<p><br></p>";
    quill.clipboard.dangerouslyPasteHTML(0, html, "api");
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

    s = s.replace(/\r\n/g, "\n").replace(/\r/g, "\n").trim();

    s = s.replace(/<\/div>/gi, "<br>");
    s = s.replace(/<div[^>]*>/gi, "");
    s = s.replace(/<\/p>/gi, "<br>");
    s = s.replace(/<p[^>]*>/gi, "");

    if (!/<br\s*\/?>/i.test(s)) {
        s = s.replace(/\n/g, "<br>");
    }

    s = s.replace(/(<br\s*\/?>\s*){3,}/gi, "<br><br>");

    return s.trim();
}
window.composeInterop = window.composeInterop || {};

window.composeInterop.replaceSignature = function (editorId, signatureHtml) {
    const host = document.getElementById(editorId);
    const quill = host?.__quill;
    const dotnetRef = host?.__dotnetRef;

    if (!quill) return;

    dotnetRef?.invokeMethodAsync("UpdateBodyHtml", quill.root.innerHTML || "");
};
function normalizePlain(text) {
    return (text || "")
        .replace(/\r\n/g, "\n")
        .replace(/\r/g, "\n")
        .replace(/[ \t]+/g, " ")
        .replace(/\n{3,}/g, "\n\n")
        .trim();
}

function plainToHtml(text) {
    const clean = normalizePlain(text);

    if (!clean) return "<p><br></p>";

    return clean
        .split(/\n{2,}/)
        .map(block => `<p>${encodeHtml(block).replace(/\n/g, "<br>")}</p>`)
        .join("");
}
function signatureToPlain(signatureHtml) {
    let s = signatureHtml || "";

    s = s.replace(/\r\n/g, "\n").replace(/\r/g, "\n");

    s = s.replace(/<\s*br\s*\/?\s*>/gi, "\n");
    s = s.replace(/<\/\s*p\s*>/gi, "\n");
    s = s.replace(/<\/\s*div\s*>/gi, "\n");

    s = s.replace(/<[^>]+>/g, "");

    return normalizePlain(s);
}

const encodeHtml = (s) =>
    (s ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;");

window.composeInterop.getEditorHtml = function (editorId) {
    const host = document.getElementById(editorId);
    const quill = host?.__quill;
    return quill?.root?.innerHTML || "";
};
window.registerComposeAutoSaveClose = function (dotnetRef) {
    if (window.__composeAutoSaveRegistered)
        return;

    window.__composeAutoSaveRegistered = true;
    window.__composeAutoSaveBusy = false;

    const closeOnce = () => {
        if (window.__composeAutoSaveBusy) return;

        window.__composeAutoSaveBusy = true;

        dotnetRef.invokeMethodAsync("CloseFromJs")
            .finally(() => {
                setTimeout(() => {
                    window.__composeAutoSaveBusy = false;
                }, 800);
            });
    };

    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape") {
            closeOnce();
        }
    });

    document.addEventListener("mousedown", function (e) {
        const dialog = document.querySelector(".mud-dialog");

        const isMudPopup =
            e.target.closest(".mud-popover") ||
            e.target.closest(".mud-list") ||
            e.target.closest(".mud-menu") ||
            e.target.closest(".mud-select");

        if (isMudPopup) return;

        if (dialog && !dialog.contains(e.target)) {
            closeOnce();
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