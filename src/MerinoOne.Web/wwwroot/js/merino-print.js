// Opens a standalone window with a fully self-contained HTML document and triggers the browser print
// dialog (Save as PDF). Used by the integration developer-docs page to export a partner guide — the
// document content (including any one-time API key) is built server-side and never leaves the browser here.
window.merinoPrintDoc = function (html) {
    try {
        var w = window.open('', '_blank');
        if (!w) {
            alert('Pop-up blocked — allow pop-ups for this site to export the PDF.');
            return;
        }
        w.document.open();
        w.document.write(html);
        w.document.close();
        w.focus();
        // Let the new document lay out before invoking print.
        setTimeout(function () { w.print(); }, 300);
    } catch (e) {
        console.error('merinoPrintDoc failed', e);
    }
};
