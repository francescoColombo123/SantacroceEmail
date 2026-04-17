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