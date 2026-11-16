# Tetitulz PDF — the tray app

The Tetitulz PDF printer with a save dialog. It sits in the notification area; print to **Tetitulz
PDF** from any program and a window asks where to save the PDF — and whether to make it PDF/A,
protect it with a password, or sign it.

```
program ──print──▶ spooler ──▶ \\.\pipe\TetitulzPdfPrinter ──▶ tray app ──▶ save dialog ──▶ PDF
```

The printer, its pipe port and the conversion are the console app's
([`Tetitulz.Pdf.VirtualPrinter`](../Tetitulz.Pdf.VirtualPrinter/README.md)): this app links its
listener, converter and installer rather than copying them. One of the two serves the printer at a
time; the tray app says so and does not start while the console listener runs.

## Install

```powershell
dotnet publish apps/Tetitulz.Pdf.PrinterTray -c Release -o "$env:LOCALAPPDATA\Programs\TetitulzPdfPrinterTray"
& "$env:LOCALAPPDATA\Programs\TetitulzPdfPrinterTray\TetitulzPdfPrinterTray.exe"
```

Right-click the red **PDF** icon: **Install the printer…** asks for elevation once and turns on
**Start at logon** (the logon start is the tray app's own; it replaces the console listener's).

## Each print

| In the dialog | What happens |
|---|---|
| File name, folder | Filled in from the document ("Microsoft Word - Invoice 42.docx" suggests *Invoice 42*) and the last folder used; an existing file is replaced only when you say so. |
| Open the PDF after saving | Opens it in your PDF viewer. |
| PDF/A-2b | ISO 19005-2 for long-term archiving: the PDF/A mark and an sRGB output intent (Windows' own profile). If the print cannot be made conformant, it is saved without the mark and the notification says why. |
| Protect with a password | AES-256; the password opens the file. PDF/A forbids encryption, so the two exclude each other. |
| Sign with | A certificate with a private key from your Windows certificate store (a smart card's prompts for its PIN); an optional reason, and an optional timestamp authority (its URL), which proves when the document was signed — a timestamp it does not give fails the save. Signed last, so nothing after it can break it — an encrypted file is signed under its own key, and the signature verifies with the password. What a verifier will refuse about the signature is said in the notification. |

Cancel saves nothing. The dialog remembers the folder, the options and the certificate — never a
password — in `%LOCALAPPDATA%\TetitulzPdfPrinter\tray-settings.json`. A notification says when the
PDF is saved; click it to open the PDF. Logs: `%LOCALAPPDATA%\TetitulzPdfPrinter\logs\tray-*.txt`.

**What stays on disk.** The print is converted in `%LOCALAPPDATA%\TetitulzPdfPrinter\pending` —
your account's own folder — and each step toward the saved file (PDF/A, the password, the signature)
is a file there, the ones before the password step unencrypted; all are deleted when the save ends,
whichever way it ends, by an ordinary delete. A print that could not be converted keeps its
PostScript in `…\failed` for fourteen days, and the logs keep document names for the same time.

What converts, and its limits, are the PostScript plugin's and the console app's — see their READMEs.
