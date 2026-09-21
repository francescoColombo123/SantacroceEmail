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

const encodeHtml = (s) =>
    (s ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;");

function normalizeEditorHtml(value) {
    let html = (value || "")
        .replace(/\r\n/g, "\n")
        .replace(/\r/g, "\n")
        .trim();

    if (!html)
        return "";

    html = html.replace(/<script[\s\S]*?<\/script>/gi, "");
    html = html.replace(/<style[\s\S]*?<\/style>/gi, "");
    html = html.replace(/<!--[\s\S]*?-->/g, "");

    const containsHtml = /<[a-z][\s\S]*?>/i.test(html);

    if (!containsHtml) {
        html = encodeHtml(html)
            .replace(/\n\n/g, "<p><br></p>")
            .replace(/\n/g, "<br>");
    } else {
        html = html.replace(/>(\s*\n+\s*)</g, "><");
    }

    html = html.replace(/(<br\s*\/?>\s*){4,}/gi, "<br><br>");
    return html.trim();
}

function normalizeSignatureHtml(signatureHtml) {
    let s = signatureHtml || "";

    s = s
        .replace(/\r\n/g, "\n")
        .replace(/\r/g, "\n")
        .trim();

    s = s.replace(/<script[\s\S]*?<\/script>/gi, "");
    s = s.replace(/<style[\s\S]*?<\/style>/gi, "");
    s = s.replace(/<!--[\s\S]*?-->/g, "");

    s = s.replace(/<div\b[^>]*>/gi, "");
    s = s.replace(/<\/div>/gi, "\n");

    s = s.replace(/<p\b[^>]*>/gi, "");
    s = s.replace(/<\/p>/gi, "\n");

    s = s.replace(/<br\s*\/?>/gi, "\n");

    s = s.replace(/\n{3,}/g, "\n\n");
    s = s.replace(/\n/g, "<br>");

    s = s.replace(/(<br\s*\/?>\s*){3,}/gi, "<br><br>");
    s = s.replace(/^(\s|<br\s*\/?>|&nbsp;)+/gi, "");
    s = s.replace(/(\s|<br\s*\/?>|&nbsp;)+$/gi, "");

    return s.trim();
}

function escapeRegExp(text) {
    return (text || "").replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function stripKnownSignatureFromHtml(html, signatureHtml) {
    if (!html || !html.trim())
        return "";

    const normalized = normalizeSignatureHtml(signatureHtml || "");
    if (!normalized)
        return html;

    const escaped = escapeRegExp(normalized);
    let s = html;

    s = s.replace(
        new RegExp(`<div[^>]*class\\s*=\\s*[\"'][^\"']*compose-signature[^\"']*[\"'][^>]*>\\s*(?:<br\\s*\\/?>\\s*)?${escaped}\\s*<\\/div>`, "gi"),
        ""
    );

    s = s.replace(
        new RegExp(`(?:<br\\s*\\/?>\\s*)?${escaped}(?:\\s|<br\\s*\\/?>|&nbsp;)*$`, "gi"),
        ""
    );

    return s.trim();
}

function stripSignatureFromHtml(html) {
    if (!html || !html.trim())
        return "";

    let s = html;

    s = s.replace(
        /<div[^>]*data-signature-start\s*=\s*["']true["'][^>]*>.*?<\/div>\s*<div[^>]*class\s*=\s*["'][^"']*compose-signature[^"']*["'][^>]*>.*?<\/div>\s*<div[^>]*data-signature-end\s*=\s*["']true["'][^>]*>.*?<\/div>/gis,
        ""
    );

    s = s.replace(/<div[^>]*data-compose-signature\s*=\s*["']true["'][^>]*>.*?<\/div>/gis, "");
    s = s.replace(/<div[^>]*data-sc-signature\s*=\s*["']1["'][^>]*>.*?<\/div>/gis, "");
    s = s.replace(/<div[^>]*class\s*=\s*["'][^"']*compose-signature[^"']*["'][^>]*>.*?<\/div>/gis, "");

    s = s.replace(/(<br\s*\/?>\s*){4,}$/gi, "<br><br>");

    return s.trim();
}

window.initQuill = (
    editorId,
    dotnetRef,
    bodyHtml,
    signatureText,
    quoteHtml
) => {
    window.destroyQuill(editorId);

    const host = document.getElementById(editorId);
    if (!host) return;

    const toolbar = document.getElementById("composeToolbar");

    const quill = new Quill(host, {
        theme: "snow",
        modules: {
            toolbar: toolbar
        }
    });

    host.__quill = quill;
    host.__dotnetRef = dotnetRef;

    const body = normalizeEditorHtml(bodyHtml || "");
    const signature = normalizeSignatureHtml(signatureText || "");

    const bodyWithoutSignature = stripKnownSignatureFromHtml(
        stripSignatureFromHtml(body),
        signature
    );

    let initialHtml =
        bodyWithoutSignature &&
        bodyWithoutSignature !== "<p><br></p>" &&
        bodyWithoutSignature !== "<p></p>"
            ? bodyWithoutSignature
            : "<p><br></p>";

    if (signature) {
        initialHtml += `
            <div class="compose-signature" data-compose-signature="true">
                <br>${signature}
            </div>
        `;
    }

    quill.clipboard.dangerouslyPasteHTML(0, initialHtml, "api");
    quill.setSelection(0, 0, "silent");

    const handler = () => {
        try {
            dotnetRef.invokeMethodAsync("UpdateBodyHtml", quill.root.innerHTML || "");
        } catch {
        }
    };

    host.__quillHandler = handler;
    quill.on("text-change", handler);
    handler();

    setTimeout(() => {
        try {
            quill.setSelection(0, 0, "silent");
            quill.focus();
        } catch {
        }
    }, 0);
};

window.composeInterop = window.composeInterop || {};

window.composeInterop.replaceSignature = function (editorId, oldSignatureHtml, signatureHtml) {
    const host = document.getElementById(editorId);
    const quill = host?.__quill;
    const dotnetRef = host?.__dotnetRef;

    if (!quill)
        return;

    const newSignature = normalizeSignatureHtml(signatureHtml || "");
    const oldSignature = normalizeSignatureHtml(oldSignatureHtml || "");

    const currentHtml = quill.root?.innerHTML || "";
    let bodyWithoutSignature = stripSignatureFromHtml(currentHtml);

    if (oldSignature) {
        bodyWithoutSignature = stripKnownSignatureFromHtml(bodyWithoutSignature, oldSignature);
    }

    if (newSignature) {
        bodyWithoutSignature = stripKnownSignatureFromHtml(bodyWithoutSignature, newSignature);
    }

    let nextHtml =
        bodyWithoutSignature &&
        bodyWithoutSignature !== "<p><br></p>" &&
        bodyWithoutSignature !== "<p></p>"
            ? bodyWithoutSignature
            : "<p><br></p>";

    if (newSignature) {
        nextHtml += `
            <div class="compose-signature" data-compose-signature="true">
                <br>${newSignature}
            </div>
        `;
    }

    const prev = quill.getSelection();

    quill.deleteText(0, quill.getLength(), "silent");
    quill.clipboard.dangerouslyPasteHTML(0, nextHtml, "api");

    if (prev) {
        const maxIndex = Math.max(0, quill.getLength() - 1);
        quill.setSelection(Math.min(prev.index, maxIndex), prev.length || 0, "silent");
    }

    dotnetRef
        ?.invokeMethodAsync("UpdateBodyHtml", quill.root?.innerHTML || "")
        .catch(() => { });
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

window.composeInterop.getEditorHtml = function (editorId) {
    const host = document.getElementById(editorId);
    const quill = host?.__quill;
    return quill?.root?.innerHTML || "";
};

window.registerComposeAutoSaveClose = function (dotnetRef) {
    // Keep the latest dialog reference (important when opening compose multiple times).
    window.__composeAutoSaveDotnetRef = dotnetRef;

    if (window.__composeAutoSaveRegistered)
        return;

    window.__composeAutoSaveRegistered = true;
    window.__composeAutoSaveBusy = false;

    const closeOnce = () => {
        if (window.__composeAutoSaveBusy) return;

        const ref = window.__composeAutoSaveDotnetRef;
        if (!ref) return;

        window.__composeAutoSaveBusy = true;

        ref.invokeMethodAsync("CloseFromJs")
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
        const target = e.target;

        if (!(target instanceof Element))
            return;

        const isMudPopup =
            target.closest(".mud-popover") ||
            target.closest(".mud-list") ||
            target.closest(".mud-menu") ||
            target.closest(".mud-select");

        if (isMudPopup) return;

        if (dialog && !dialog.contains(target)) {
            closeOnce();
        }
    });
};

window.initQuillTask = (editorId, toolbarId, dotnetRef, initialHtml) => {
    const editorEl = document.getElementById(editorId);
    const toolbarEl = document.getElementById(toolbarId);

    if (!editorEl || !toolbarEl) return;

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

    editorEl.__quill = quill;
};

window.printSingleEmail = function (subject, mail) {

    if (!mail) {
        alert("Nessuna email da stampare.");
        return;
    }

    // Apre una pagina about:blank dedicata alla stampa
    const win = window.open(
        "",
        "_blank",
        "width=1000,height=800"
    );

    if (!win) {
        alert("Il browser ha bloccato la finestra di stampa.");
        return;
    }

    const escapeHtml = value => {
        if (value === null || value === undefined)
            return "";

        return String(value)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    };


    // =========================
    // DESTINATARI
    // =========================

    let recipients = "";

    if (mail.to) {
        recipients += `
            <div class="recipient">
                <strong>A:</strong> ${escapeHtml(mail.to)}
            </div>`;
    }

    if (mail.cc) {
        recipients += `
            <div class="recipient">
                <strong>Cc:</strong> ${escapeHtml(mail.cc)}
            </div>`;
    }

    if (mail.bcc) {
        recipients += `
            <div class="recipient">
                <strong>Ccn:</strong> ${escapeHtml(mail.bcc)}
            </div>`;
    }


    // =========================
    // ALLEGATI
    // =========================

    let attachments = "";

    if (mail.attachments && mail.attachments.length > 0) {

        attachments = `
            <div class="attachments">

                ${mail.attachments
                .map(a => `
                        <span class="attachment">
                            📎 ${escapeHtml(a.name)}
                        </span>
                    `)
                .join("")}

            </div>`;
    }


    // =========================
    // OGGETTO
    // =========================

    const safeSubject = escapeHtml(
        subject || "(nessun oggetto)"
    );


    // =========================
    // CONTENUTO MAIL
    // =========================

    const content = `
        <section class="message">

            <header class="message-header">

                <div class="header-row">

                    <strong class="sender">
                        ${escapeHtml(mail.sender)}
                    </strong>

                    <span class="date">
                        ${escapeHtml(mail.date)}
                    </span>

                </div>

                ${recipients}

            </header>


            <div class="message-body">
                ${mail.bodyHtml || ""}
            </div>


            ${attachments}

        </section>
    `;


    // =========================
    // DOCUMENTO DI STAMPA
    // =========================

    const printDocument = win.document;

    printDocument.open();

    printDocument.write(`
<!DOCTYPE html>

<html>

<head>

    <meta charset="utf-8">

    <title>${safeSubject}</title>

    <style>

        * {
            box-sizing: border-box;
        }


        html,
        body {
            margin: 0;
            padding: 0;

            background: white;
            color: #202124;
        }


        body {
            font-family: Arial, Helvetica, sans-serif;

            font-size: 13px;
            line-height: 1.5;

            padding: 25px;
        }


        .print-container {
            width: 100%;
        }


        /* =========================
           OGGETTO
           ========================= */

        .subject {

            font-size: 20px;
            font-weight: 600;

            margin: 0 0 20px 0;

            padding-bottom: 10px;

            border-bottom: 1px solid #dadce0;

            overflow-wrap: anywhere;
        }


        /* =========================
           MAIL
           ========================= */

        .message {
            width: 100%;
        }


        .message-header {
            margin-bottom: 18px;
        }


        .header-row {

            display: flex;

            justify-content: space-between;

            align-items: flex-start;

            gap: 20px;
        }


        .sender {

            font-size: 14px;

            font-weight: 600;

            overflow-wrap: anywhere;
        }


        .date {

            color: #5f6368;

            white-space: nowrap;

            font-size: 12px;
        }


        .recipient {

            margin-top: 2px;

            color: #5f6368;

            font-size: 12px;

            overflow-wrap: anywhere;
        }


        /* =========================
           CORPO EMAIL
           ========================= */

        .message-body {

            margin-top: 15px;

            overflow-wrap: anywhere;
        }


        .message-body img {

            max-width: 100% !important;

            height: auto !important;
        }


        .message-body table {

            max-width: 100% !important;
        }


        .message-body pre {

            white-space: pre-wrap !important;

            overflow-wrap: anywhere;
        }


        /* =========================
           CITAZIONE APERTA
           ========================= */

        .print-quoted {

            margin-top: 12px;
        }


        /* =========================
           CITAZIONE CHIUSA
           ========================= */

        .print-quoted-hidden {

            margin-top: 6px;

            color: #5f6368;

            font-size: 11px;
        }


        /* =========================
           ALLEGATI
           ========================= */

        .attachments {

            display: flex;

            flex-wrap: wrap;

            gap: 8px;

            margin-top: 20px;

            padding-top: 12px;

            border-top: 1px solid #eeeeee;
        }


        .attachment {

            display: inline-block;

            border: 1px solid #dadce0;

            border-radius: 16px;

            padding: 5px 10px;

            font-size: 12px;
        }


        /* =========================
           PAGINA
           ========================= */

        @page {

            size: auto;

            margin: 15mm;
        }


        /* =========================
           STAMPA
           ========================= */

        @media print {

            body {

                padding: 0;
            }


            .message-header,
            .attachments,
            img {

                break-inside: avoid;

                page-break-inside: avoid;
            }
        }

    </style>

</head>


<body>

    <main class="print-container">

        <h1 class="subject">
            ${safeSubject}
        </h1>

        ${content}

    </main>

</body>

</html>
    `);


    printDocument.close();


    // =========================
    // STAMPA
    // =========================

    let printed = false;


    const printNow = () => {

        if (printed)
            return;


        printed = true;


        setTimeout(() => {

            try {

                win.focus();

                win.print();

            }
            catch (error) {

                console.error(
                    "Errore durante la stampa:",
                    error
                );
            }

        }, 250);
    };


    // =========================
    // ASPETTA LE IMMAGINI
    // =========================

    const images = Array.from(
        printDocument.images
    );


    // Nessuna immagine:
    // possiamo stampare subito

    if (images.length === 0) {

        printNow();

        return;
    }


    let pending = images.length;


    const imageFinished = () => {

        pending--;

        if (pending <= 0) {

            printNow();
        }
    };


    images.forEach(img => {

        if (img.complete) {

            imageFinished();

        }
        else {

            img.addEventListener(
                "load",
                imageFinished,
                {
                    once: true
                }
            );


            img.addEventListener(
                "error",
                imageFinished,
                {
                    once: true
                }
            );
        }
    });


    // Sicurezza:
    // se qualche immagine non termina il caricamento,
    // stampa comunque dopo 3 secondi

    setTimeout(
        printNow,
        3000
    );
};