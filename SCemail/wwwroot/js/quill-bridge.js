// 🔒 Patch anti-crash Quill 1.3.6 (zombie observer)
(function () {
    if (window.__quillPatched) return;
    window.__quillPatched = true;

    function safe(fnName) {
        const p = window.Quill && window.Quill.import && window.Quill.import('parchment');
        // Se non riesce, patchiamo direttamente prototype di Update
    }

    // Patch diretta sul metodo update di Scroll (dove crasha emitter.emit)
    try {
        const Scroll = Quill.import('blots/scroll');
        const original = Scroll.prototype.update;

        Scroll.prototype.update = function (...args) {
            // Se emitter è già andato, non crashare
            if (!this.emitter || typeof this.emitter.emit !== "function") return;
            return original.apply(this, args);
        };
    } catch (e) {
        // se Quill non è ancora caricato, ritenta appena disponibile
        const t = setInterval(() => {
            try {
                if (!window.Quill) return;
                clearInterval(t);
                const Scroll = Quill.import('blots/scroll');
                const original = Scroll.prototype.update;
                Scroll.prototype.update = function (...args) {
                    if (!this.emitter || typeof this.emitter.emit !== "function") return;
                    return original.apply(this, args);
                };
            } catch { }
        }, 50);
        setTimeout(() => clearInterval(t), 5000);
    }
})();
