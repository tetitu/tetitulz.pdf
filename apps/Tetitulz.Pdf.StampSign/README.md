# Tetitulz Stamp & Sign

A small Windows app for one job: open a PDF, place your stamp picture where it belongs, and sign it
visibly with the certificate on your smart card (or any certificate with a private key in your
Windows store). .NET Framework 4.8, so it runs on any supported Windows without installing .NET.

```powershell
dotnet build apps/Tetitulz.Pdf.StampSign -c Release
apps\Tetitulz.Pdf.StampSign\bin\Release\net48\tetitulz-stampsign.exe path\to\file.pdf
```

## Use

1. **Open** a PDF (or drag one onto the window).
2. **Add stamp** puts your stamp picture on the page in view; drag a PNG onto the page to place another
   picture where you drop it. Drag a stamp to move it, a corner to resize it (Shift stretches it),
   arrow keys nudge it, Delete removes it.
3. **Sign & Save** draws the stamps into the pages (one appended revision), adds your signature
   picture at its fixed place and signs; your smart card asks for its PIN. **Save without signing**
   writes the stamps alone.

The first run asks for the stamp picture, the signature picture and where the signature goes
(page, corner, size and offsets in millimetres, shown on an A4 preview), and the certificate.
Optional: a reason, a location, and a **timestamp authority** (e.g. `http://timestamp.digicert.com`)
that proves when you signed, so the signature still verifies after the certificate expires.

- A document that is **already signed** is said to be so when it opens: your stamps and signature
  are added after that signature, which stays valid for what it signed, and a reader lists your
  additions as changes made after it.
- The signatures' verdicts show in a banner over the page — the document's own, and yours once saved.
- What a verifier would refuse about your signature (an expired certificate, one issued for another
  purpose) is said after signing.
- The original file is never overwritten: a save asks for a new name.

Settings and the copied pictures are in `%APPDATA%\Tetitulz\StampSign` (or the folder
`TETITULZ_STAMPSIGN_HOME` names, for a portable copy). Tests: `tests/Tetitulz.Pdf.StampSign.Tests`
(the placement geometry and the stamp writer, by rendering).
