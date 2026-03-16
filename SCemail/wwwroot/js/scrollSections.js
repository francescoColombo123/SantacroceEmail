window.scrollSections = window.scrollSections || {};

window.scrollSections.attach = function (elementId, dotNetRef, methodName, thresholdPx) {
    const el = document.getElementById(elementId);
    if (!el) return;

    const threshold = thresholdPx ?? 120;

    const handler = () => {
        const dist = el.scrollHeight - (el.scrollTop + el.clientHeight);
        if (dist <= threshold) {
            dotNetRef.invokeMethodAsync(methodName, elementId);
        }
    };

    // evita doppio attach se rerender
    el.__scrollSectionsAttached = el.__scrollSectionsAttached || false;
    if (el.__scrollSectionsAttached) return;

    el.addEventListener("scroll", handler);
    el.__scrollSectionsAttached = true;
};