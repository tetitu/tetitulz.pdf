# Tetitulz PDF Viewer (WPF)

A desktop PDF editor around the library's own viewer control (`Tetitulz.Pdf.WinForms.PdfViewer`):
read, fill forms with their scripts, mark up, organise pages, sign, redact, protect and convert.
Windows, .NET 10.

```powershell
dotnet run --project apps/Tetitulz.Pdf.WpfViewer -- path\to\file.pdf
```

Installed from a 32-bit or a 64-bit MSI (`apps/installers`, `build.ps1 -App Viewer`).

## In Windows

- **The default PDF app.** Home → Default app opens Windows' Default apps at the viewer, where you
  choose it for `.pdf` (Windows lets no program make that choice itself). Installed, the viewer is
  already registered; run from a build, it registers itself for the current user first.
- **File Explorer's preview pane** shows PDFs through the viewer (the installer's options): pages fitted
  to the pane, the floating page and zoom controls, the pane's light or dark colours; read-only — no
  links followed, nothing printed or filled. The preview handler is this same program started by COM
  with `-Embedding` (`Shell/PreviewServer.cs`), an out-of-process server that exits after two idle
  minutes; so the 32-bit build previews for 64-bit Explorer as well. A password-protected PDF, a file
  that is no PDF and one over 256 MB are described, not shown.
- `Shell/ShellIdentity.cs` holds the names Windows knows the viewer by — the file type, the Default apps
  entry, the preview handler's class; `apps/installers/Viewer.wxs` writes the same names, and a test
  holds the two together.

## How it works

- **Every change is an undo step**, named in the Undo button's tooltip (Ctrl+Z, Ctrl+Y). Edits run
  one at a time: a second command waits for the first and works on its result. Undo keeps up to
  256 MB of earlier versions, the oldest dropped first.
- **Tools on the page.** A command that needs a place — highlight, underline, strike out, rectangle,
  text box, stamp, link, picture, crop, a signature, a redaction — asks you to drag out a box (or
  click a point) on the page. Esc or a right click cancels.
- **Marking selected text.** With text selected, highlight, underline and strike out mark it
  instead: one annotation a page, a quad a line, the selected text as its comment, all in one undo
  step. With nothing selected they take a dragged box.
- **Save and Save As.** Save writes back to the file the document came from; a document converted
  from Word, merged or imported asks for a name. Closing, opening another file or merging asks first
  when there are unsaved changes, form values typed on the page included.
- **Password-protected files stay protected.** A file opened under a password is edited as plaintext
  and written back under its own key and permissions; an unchanged file is written back byte for
  byte. Removing the password is a command of its own (Protect → Remove Password). Encrypt uses
  AES-256; once saved, the document carries on under that password.
- **Signing.** Protect → Sign asks for a certificate from the Windows store (a smart card asks for
  its PIN) or a `.pfx` file, a reason, a place and an optional timestamp authority, then for the box
  the signature shows in — a picture you choose, or your name and the date. A click on an empty
  signature field signs that field. What a verifier would refuse about the signature (an expired
  certificate, a weak digest) is said in the status line. A password-protected document is signed
  under its own key, invisibly, and only when unchanged since it was opened.
- **Trust.** Protect → Trust chooses what a signature must chain to: Windows' trusted roots (the
  default), the certificates in a folder, or nothing (integrity only). The banner over the page and
  Verify both use it.
- **Redaction** removes content, never covers it. Mark areas by dragging, or every place a text is
  drawn, review the marks on the pages, then Apply: one pass removes everything under them and
  writes a new file. Content the engine cannot remove without a leak is refused, and the document
  stays as it was.
- **Organize** shows every page: select several, drag to reorder, turn, delete, insert another
  PDF's pages, or extract a selection to its own file. OK applies it as one step.
- **A rewrite of the whole file** (optimize, repair, sanitize, linearize, flatten, convert to PDF/A,
  4-up, organize, redact, encrypt) on a signed document asks first: the signatures do not survive it.

Settings (trust choice, signing defaults — never a password) are in
`%LOCALAPPDATA%\TetituPdfViewer\settings.json`, recent files beside them.

## Limits

- Markup is placed by a dragged box, not by selected text.
- The Info and Text tabs under the page show reports and extracted text as plain text.
- OCR needs Tesseract's `tessdata`; without it the Text tab says why OCR did not run.

Tests: `tests/Tetitulz.Pdf.WpfViewer.Tests` (the edit session and the save path, without a window).
