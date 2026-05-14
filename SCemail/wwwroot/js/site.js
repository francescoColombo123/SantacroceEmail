window.checkMailScrollBottom = function () {
    const el = document.getElementById('mailScroll');
    if (!el) return false;
    return el.scrollTop + el.clientHeight >= el.scrollHeight - 50;
};

window.initMailScrollListener = (dotnetHelper, element) => {
    if (!element || typeof element.addEventListener !== "function") {
        console.warn("⚠️ Elemento non pronto per lo scroll listener:", element);
        return;
    }
    console.log("✅ Scroll listener inizializzato su:", element);

    element.addEventListener("scroll", () => {
        const atBottom = element.scrollTop + element.clientHeight >= element.scrollHeight - 100;
        if (atBottom) {
            console.log("📩 Scroll arrivato in fondo → notifico Blazor");
            dotnetHelper.invokeMethodAsync("OnMailScrolledToBottom");
        }
    }, { passive: true });
};

window.recips = {
    getInput: function (hostId) {
        const el = document.querySelector(`#${hostId} input`);
        return el ? el.value : '';
    },
    clearInput: function (hostId) {
        const el = document.querySelector(`#${hostId} input`);
        if (el) el.value = '';
    }
};

window.focusElementById = function (id) {
    console.log("📬 mailHelpers.js caricato correttamente!");

    const el = document.getElementById(id);
    if (el) setTimeout(() => el.focus(), 50);
}; 

// ==== Attachment popup helpers (Blazor) ====
window.openInline = (url) => window.open(url, "_blank", "noopener,noreferrer");
window._namedWins = window._namedWins || {};
window.openNewTab = (url) => {
    window.open(url, "_blank", "noopener,noreferrer");
};
window.openNamed = (name) => {
    const w = window.open("about:blank", name);
    if (!w) {
        console.warn("❌ Popup bloccato: impossibile aprire la tab");
        return false; // ✅ boolean
    }
    window._namedWins[name] = w;
    try { w.document.title = "Caricamento allegato…"; } catch { }
    return true; // ✅ boolean
};

window.navigateNamed = (name, url) => {
    const abs = new URL(url, window.location.origin).href;
    const w = window._namedWins[name] || window.open("", name);
    if (!w) {
        console.warn("❌ Finestra non disponibile (popup bloccato?)");
        return false; // ✅ boolean (opzionale)
    }
    w.location.href = abs;
    w.focus();
    return true; // ✅ boolean (opzionale)
};

window.openPopupWindow = (url) => {
    window.open(
        url,
        "_blank",
        "width=1500,height=900,left=80,top=60,resizable=yes,scrollbars=yes"
    );
};

console.log("✅ openNamed loaded:", typeof window.openNamed);