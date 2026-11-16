// The page's one script of its own, in a file so the host's content security policy can refuse
// every inline script. Blazor marshals byte[] to a Uint8Array.
window.wasmSign = {
    save: function (name, bytes) {
        const url = URL.createObjectURL(new Blob([bytes], { type: "application/pdf" }));
        const a = document.createElement("a");
        a.href = url;
        a.download = name;
        a.click();
        URL.revokeObjectURL(url);
    }
};
