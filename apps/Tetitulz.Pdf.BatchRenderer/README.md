# TetitulzBatchRender

Renders every page of every PDF under a folder and says what failed — a quick check of the renderer
over a corpus, or a way to turn a folder of PDFs into page images. .NET 10, Windows and Linux.

```powershell
dotnet run --project apps/Tetitulz.Pdf.BatchRenderer -c Release -- path\to\folder --dpi 150 --png --out path\to\images
```

| Option | |
|---|---|
| `--dpi N` | Resolution, 1–2400 (default 150). |
| `--png`, `--jpg` | Write each page as an image, keeping the folder's subfolders: `name_p3.png`, or `name.png` for a one-page file. Without either, pages are rendered and discarded. |
| `--out DIR` | Where the images go (default `<folder>/png` or `<folder>/jpg`). |
| `--password-env NAME` | The password for encrypted files, read from that environment variable. Without it an encrypted file renders when its user password is empty and is reported otherwise. |

Each file is opened once and all its pages rendered through one render session, so a long document
costs its pages and not its pages squared. The summary lists every file and page that failed, and
how many files the renderer reported approximations for; the log (in
`%LOCALAPPDATA%\TetitulzBatchRender\logs`) has the detail. Exit code: 0 every page rendered, 1 a
usage error, 2 a file or a page failed.
