// POSTs to a same-origin report-download proxy (e.g. /files/packing-list/{id}/download), saves the response as a file and
// reports whether the server also auto-attached the report (X-Report-Attached: true). Used by PdfPreviewDialog.
// Returns { ok, status, attached }; never throws (network failure => { ok:false, status:0, attached:false }).
window.merinoDownload = {
    fetchAndSave: async function (url, fallbackFileName) {
        try {
            const resp = await fetch(url, { method: 'POST', credentials: 'same-origin' });
            if (!resp.ok) return { ok: false, status: resp.status, attached: false };

            const blob = await resp.blob();
            let fileName = fallbackFileName || 'download.pdf';
            const cd = resp.headers.get('Content-Disposition');
            if (cd) {
                // RFC 5987 filename*=UTF-8''... wins over the plain filename=.
                const star = /filename\*\s*=\s*(?:UTF-8|utf-8)?'[^']*'([^;]+)/i.exec(cd);
                const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(cd);
                try {
                    if (star) fileName = decodeURIComponent(star[1].trim());
                    else if (plain) fileName = plain[1].trim();
                } catch (e) {
                    if (plain) fileName = plain[1].trim();
                }
            }

            const objectUrl = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = objectUrl;
            a.download = fileName;
            a.style.display = 'none';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(function () { URL.revokeObjectURL(objectUrl); }, 1000);

            return { ok: true, status: resp.status, attached: resp.headers.get('X-Report-Attached') === 'true' };
        } catch (e) {
            console.error('merinoDownload.fetchAndSave failed', e);
            return { ok: false, status: 0, attached: false };
        }
    }
};
