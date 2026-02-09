window.initQuillTask = (editorId, toolbarId, dotnetRef) => {
    const editorEl = document.getElementById(editorId);
    const toolbarEl = document.getElementById(toolbarId);
    if (!editorEl || !toolbarEl) return;

    // toolbar minimale
    toolbarEl.innerHTML = `
    <span class="ql-formats">
      <button class="ql-bold"></button>
      <button class="ql-italic"></button>
      <button class="ql-underline"></button>
    </span>
    <span class="ql-formats">
      <button class="ql-list" value="bullet"></button>
      <button class="ql-list" value="ordered"></button>
    </span>
    <span class="ql-formats">
      <button class="ql-link"></button>
      <button class="ql-clean"></button>
    </span>
  `;

    const q = new Quill(editorEl, {
        theme: "snow",
        modules: { toolbar: toolbarEl },
    });

    // spinge HTML verso Blazor
    const push = () => {
        dotnetRef.invokeMethodAsync("UpdateTaskHtml", q.root.innerHTML);
    };
    q.on("text-change", push);

    // prima sync
    push();

    // opzionale: conserva istanza per destroy
    window.__taskQuill = window.__taskQuill || {};
    window.__taskQuill[editorId] = q;
};

window.destroyQuillTask = (editorId) => {
    if (!window.__taskQuill || !window.__taskQuill[editorId]) return;
    delete window.__taskQuill[editorId];
};
