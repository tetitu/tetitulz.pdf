# Tetitulz PDF Splitter

Takes pages out of a PDF into new files. Thumbnails and a print-dialog range box (`1-3, 7, 12-`)
stay in step: click pages, or type them. .NET Framework 4.8.

```powershell
dotnet build apps/Tetitulz.Pdf.Splitter -c Release
apps\Tetitulz.Pdf.Splitter\bin\Release\net48\tetitulz-splitter.exe path\to\file.pdf
```

## What it writes

| Write | Result |
|---|---|
| The selected pages, one file | One file, the pages in order |
| Each part of the range, a file each | `1-3, 7, 9-` makes three files, one per comma-separated part |
| The whole document, every N pages | Files of N pages each, the last one shorter |
| The whole document, files under N MB | As few files as keep each at or below N megabytes; a single page larger than that is a file of its own, and said |

Several files are named from the name you give: `report_part01_pages_1-3.pdf`, `report_part02_pages_7.pdf`…
Existing files are replaced only when you say so.

- **An encrypted PDF** opens with its empty password when it has one, else the one you type; what
  you extract is protected the same way — under the original's own key and permissions where that
  can be kept, otherwise under the password that opened it. Nothing is written in the clear.
- What an extract leaves behind (page labels, a structure tree, form fields of other pages) is said
  in the status line.
- Ctrl+mouse wheel, or − and +, resizes the thumbnails; only the ones in view are drawn, so a long
  document opens at once.

Tests: `tests/Tetitulz.Pdf.Splitter.Tests` (the range grammar, the planning and the encrypted extract).
