window.taskEditor = (function () {
    const editors = new Map();

    function init(id) {
        const el = document.getElementById(id);
        if (!el) return;

        const q = new Quill(el, {
            theme: "snow",
            modules: {
                toolbar: false // toolbar la fai tu con i bottoni Mud
            }
        });

        editors.set(id, q);
    }

    function destroy(id) {
        editors.delete(id);
    }

    function setHtml(id, html) {
        const q = editors.get(id);
        if (!q) return;
        q.clipboard.dangerouslyPasteHTML(html || "");
    }

    function getHtml(id) {
        const q = editors.get(id);
        if (!q) return "";
        return q.root.innerHTML || "";
    }

    function bold(id) {
        const q = editors.get(id);
        if (!q) return;
        const f = q.getFormat();
        q.format("bold", !f.bold);
        q.focus();
    }

    function bullet(id) {
        const q = editors.get(id);
        if (!q) return;
        const f = q.getFormat();
        // toggle lista
        q.format("list", f.list === "bullet" ? false : "bullet");
        q.focus();
    }

    return { init, destroy, setHtml, getHtml, bold, bullet };
})();
