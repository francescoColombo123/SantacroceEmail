window.mentionsInterop = {
    bindMentionKeys: function (inputId) {
        const input = document.getElementById(inputId);
        if (!input) return;

        if (input.dataset.mentionBound === "1") return;
        input.dataset.mentionBound = "1";

        input.addEventListener("keydown", function (e) {
            const popupOpen = document.body.getAttribute("data-mention-open") === "1";
            if (!popupOpen) return;

            const keys = ["ArrowUp", "ArrowDown", "Enter", "Tab", "Escape"];
            if (keys.includes(e.key)) {
                e.preventDefault();
            }
        });
    },

    setMentionOpen: function (isOpen) {
        document.body.setAttribute("data-mention-open", isOpen ? "1" : "0");
    }
};

window.mentionsInterop = window.mentionsInterop || {};

window.mentionsInterop.setValue = function (id, value) {
    const el = document.getElementById(id);
    if (!el) return;

    el.value = value ?? "";
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.focus();
};

window.mentionsInterop.scrollActiveIntoView = function () {
    const active = document.querySelector('.mention-popup [data-active="true"]');

    if (active) {
        active.scrollIntoView({
            block: "nearest"
        });
    }
};

