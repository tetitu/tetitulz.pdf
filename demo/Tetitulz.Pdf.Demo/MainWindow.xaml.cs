// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.DocxToPdf;
using Tetitulz.Pdf.HtmlToPdf;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.Plugins.EthereumSign;
using Tetitulz.Pdf.Plugins.PageEdits;
namespace Tetitulz.Pdf.Demo
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogOutput.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
                LogOutput.ScrollToEnd();
            });
        }

        private void SetBusy(bool busy)
            => Dispatcher.Invoke(() => GlobalProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed);

        private string BrowseOpenFile(string filter = "All files|*.*", bool multi = false)
        {
            var dlg = new OpenFileDialog { Filter = filter, Multiselect = multi };
            bool? r = dlg.ShowDialog(this);
            if (r == true)
            {
                if (multi) return string.Join("|", dlg.FileNames);
                return dlg.FileName;
            }
            return null;
        }

        private string BrowseSaveFile(string filter = "PDF files|*.pdf")
        {
            var dlg = new SaveFileDialog { Filter = filter, OverwritePrompt = true };
            bool? r = dlg.ShowDialog(this);
            return r == true ? dlg.FileName : null;
        }

        private string BrowseFolder()
        {
            using (var fd = new System.Windows.Forms.FolderBrowserDialog())
            {
                var result = fd.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                    return fd.SelectedPath;
            }
            return null;
        }

        // ---------------- DOCX Tab ----------------
        private void BrowseDocxInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("DOCX files|*.docx|All files|*.*");
            if (!string.IsNullOrEmpty(path)) DocxInputPath.Text = path;
        }

        private void DocxEngine_Checked(object sender, RoutedEventArgs e)
            => LibreOfficePathRow.Visibility = DocxEngineLibreOffice.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;

        private void BrowseLibreOfficePath_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("Executable|*.exe|All files|*.*");
            if (!string.IsNullOrEmpty(path)) LibreOfficePathBox.Text = path;
        }

        private void BrowseDocxOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) DocxOutputPath.Text = path;
        }

        private async void ConvertDocxToPdf_Click(object sender, RoutedEventArgs e)
        {
            string input = DocxInputPath.Text;
            string output = DocxOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Provide input and output paths."); return; }

            var options = new DocxToPdfOptions();
            if (DocxEngineLibreOffice.IsChecked == true) options.Engine = ConversionEngine.LibreOffice;
            else if (DocxEngineMsWord.IsChecked == true) options.Engine = ConversionEngine.MicrosoftWord;
            else options.Engine = ConversionEngine.BuiltIn;
            if (!string.IsNullOrWhiteSpace(LibreOfficePathBox.Text)) options.LibreOfficePath = LibreOfficePathBox.Text;

            Log($"Starting DOCX -> PDF ({options.Engine})...");
            SetBusy(true);
            await Task.Run(() =>
            {
                try
                {
                    DocxToPdfConverter.Convert(input, output, options);
                    Log($"Converted DOCX -> PDF: {output}");
                }
                catch (Exception ex)
                {
                    Log($"Error converting DOCX: {ex.Message}");
                }
            });
            SetBusy(false);
        }

        // ---------------- HTML Tab ----------------
        private void HtmlSource_Checked(object sender, RoutedEventArgs e)
        {
            if (HtmlInputBox == null) return;
            if (HtmlSourceFile?.IsChecked == true)
            {
                HtmlInputBox.Text = "";
                HtmlInputBox.IsReadOnly = false;
                HtmlBrowseBtn.Visibility = Visibility.Visible;
            }
            else if (HtmlSourceUrl?.IsChecked == true)
            {
                HtmlInputBox.Text = "https://";
                HtmlInputBox.IsReadOnly = false;
                HtmlBrowseBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                HtmlInputBox.Text = "<html><body><p>Paste HTML here</p></body></html>";
                HtmlInputBox.IsReadOnly = false;
                HtmlBrowseBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void BrowseHtmlInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("HTML files|*.html;*.htm|All files|*.*");
            if (!string.IsNullOrEmpty(path)) HtmlInputBox.Text = path;
        }

        private void BrowseHtmlOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) HtmlOutputPath.Text = path;
        }

        private async void InstallBrowsers_Click(object sender, RoutedEventArgs e)
        {
            Log("Installing Playwright browsers (may take a while)...");
            SetBusy(true);
            await Task.Run(() =>
            {
                try
                {
                    // HtmlToPdfConverter.InstallBrowsers, which this called until 2026-08-28, has
                    // ALWAYS THROWN since browser installation moved to this plugin — so the
                    // button did nothing but log its own deprecation message as an error. The
                    // CS0618 warning said so and went unread under 372 others.
                    Tetitulz.Pdf.Plugins.HtmlBrowser.PlaywrightHtmlBrowserPlugin.InstallBrowsers();
                    Log("Playwright browsers installed.");
                }
                catch (Exception ex)
                {
                    Log($"Error installing browsers: {ex.Message}");
                }
            });
            SetBusy(false);
        }

        private async void ConvertHtmlToPdf_Click(object sender, RoutedEventArgs e)
        {
            string output = HtmlOutputPath.Text;
            if (string.IsNullOrEmpty(output)) { Log("Select output PDF path."); return; }

            var htmlOptions = new HtmlToPdfOptions();
            if (HtmlEngineFirefox.IsChecked == true) htmlOptions.Engine = BrowserEngine.Firefox;
            else if (HtmlEngineWebKit.IsChecked == true) htmlOptions.Engine = BrowserEngine.WebKit;
            else htmlOptions.Engine = BrowserEngine.Chromium;

            Log($"Starting HTML -> PDF ({htmlOptions.Engine})...");
            SetBusy(true);
            await Task.Run(() =>
            {
                try
                {
                    if (HtmlSourceUrl.IsChecked == true)
                        HtmlToPdfConverter.ConvertUrl(HtmlInputBox.Text, output, htmlOptions);
                    else if (HtmlSourceFile.IsChecked == true)
                        HtmlToPdfConverter.ConvertFile(HtmlInputBox.Text, output, htmlOptions);
                    else
                        HtmlToPdfConverter.ConvertHtml(HtmlInputBox.Text, output, htmlOptions);
                    Log($"HTML -> PDF written to: {output}");
                }
                catch (Exception ex)
                {
                    Log($"Error converting HTML: {ex.Message}");
                }
            });
            SetBusy(false);
        }

        // ---------------- Merge / Split / Optimize / N-up ----------------
        private void MergeAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf|All files|*.*", Multiselect = true };
            bool? r = dlg.ShowDialog(this);
            if (r == true)
            {
                foreach (var f in dlg.FileNames) MergeFileList.Items.Add(f);
            }
        }

        private void BrowseMergeOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) MergeOutputPath.Text = path;
        }

        private async void MergePdfs_Click(object sender, RoutedEventArgs e)
        {
            var files = MergeFileList.Items.Cast<string>().ToArray();
            string output = MergeOutputPath.Text;
            if (files.Length == 0 || string.IsNullOrEmpty(output)) { Log("Select input files and output path."); return; }

            Log($"Merging {files.Length} PDFs...");
            SetBusy(true);
            await Task.Run(() =>
            {
                try
                {
                    PdfMerger.Merge(output, files);
                    Log($"Merged into {output}");
                }
                catch (Exception ex)
                {
                    Log($"Error merging PDFs: {ex.Message}");
                }
            });
            SetBusy(false);
        }

        private void MergeClear_Click(object sender, RoutedEventArgs e)
        {
            MergeFileList.Items.Clear();
        }

        private void BrowseSplitInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) SplitInputPath.Text = path;
        }

        private void BrowseSplitOutputDir_Click(object sender, RoutedEventArgs e)
        {
            var p = BrowseFolder();
            if (!string.IsNullOrEmpty(p)) SplitOutputDir.Text = p;
        }

        private async void SplitPdf_Click(object sender, RoutedEventArgs e)
        {
            string input = SplitInputPath.Text;
            string outputDir = SplitOutputDir.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(outputDir)) { Log("Select input PDF and output directory."); return; }

            // If range fields are filled, use SplitRange; otherwise split all pages
            string fromText = SplitRangeFrom.Text.Trim();
            string toText = SplitRangeTo.Text.Trim();
            if (!string.IsNullOrEmpty(fromText) && !string.IsNullOrEmpty(toText))
            {
                if (!int.TryParse(fromText, out int from) || !int.TryParse(toText, out int to))
                { Log("Invalid page range values."); return; }
                string rangeOutput = Path.Combine(outputDir, $"split_{from}-{to}.pdf");
                Log($"Splitting PDF pages {from}-{to}...");
                SetBusy(true);
                await Task.Run(() =>
                {
                    try
                    {
                        PdfSplitter.SplitRange(input, rangeOutput, from, to);
                        Log($"Range split written to {rangeOutput}");
                    }
                    catch (Exception ex)
                    {
                        Log($"Error splitting PDF range: {ex.Message}");
                    }
                });
                SetBusy(false);
            }
            else
            {
                Log("Splitting PDF into individual pages...");
                SetBusy(true);
                await Task.Run(() =>
                {
                    try
                    {
                        PdfSplitter.Split(input, outputDir);
                        Log($"Split pages written to {outputDir}");
                    }
                    catch (Exception ex)
                    {
                        Log($"Error splitting PDF: {ex.Message}");
                    }
                });
                SetBusy(false);
            }
        }

        private void BrowseOptimizeInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) OptimizeInputPath.Text = path;
        }

        private void BrowseOptimizeOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) OptimizeOutputPath.Text = path;
        }

        private async void OptimizePdf_Click(object sender, RoutedEventArgs e)
        {
            string input = OptimizeInputPath.Text;
            string output = OptimizeOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            var options = new OptimizationOptions
            {
                CompressStreams = OptCompressStreams.IsChecked == true,
                RemoveDuplicates = OptRemoveDuplicates.IsChecked == true,
                RemoveUnusedObjects = OptRemoveUnused.IsChecked == true,
                RemoveMetadata = OptRemoveMetadata.IsChecked == true
            };
            if (OptImageOptEnabled.IsChecked == true)
            {
                int presetIndex = OptImagePreset.SelectedIndex;
                switch (presetIndex)
                {
                    case 1: options.ImageOptimization = ImageOptimizationOptions.WebPreset; break;
                    case 2: options.ImageOptimization = ImageOptimizationOptions.BalancedPreset; break;
                    case 3: options.ImageOptimization = ImageOptimizationOptions.PrintPreset; break;
                    default:
                        if (!int.TryParse(OptImageQuality.Text, out int q) || q < 1 || q > 100)
                        {
                            q = 75;
                            Log($"Image quality '{OptImageQuality.Text}' is invalid — using default 75.");
                        }
                        options.ImageOptimization = new ImageOptimizationOptions
                        {
                            Enabled = true,
                            TargetFormat = ImageFormat.Jpeg,
                            Quality = q
                        };
                        break;
                }
            }

            Log("Optimizing PDF...");
            SetBusy(true);
            await Task.Run(() =>
            {
                try
                {
                    PdfOptimizer.Optimize(input, output, options);
                    Log($"Optimized PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log($"Error optimizing PDF: {ex.Message}");
                }
            });
            SetBusy(false);
        }

        private void BrowseNupInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) NupInputPath.Text = path;
        }

        private void BrowseNupOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) NupOutputPath.Text = path;
        }

        private async void CreateNup_Click(object sender, RoutedEventArgs e)
        {
            string input = NupInputPath.Text;
            string output = NupOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            int layout = NupLayout.SelectedIndex; // 0=2-up, 1=4-up, 2=6-up, 3=9-up
            Log($"Creating {(layout == 0 ? "2" : layout == 1 ? "4" : layout == 2 ? "6" : "9")}-up imposition...");
            await Task.Run(() =>
            {
                try
                {
                    using (var inStream = File.OpenRead(input))
                    using (var outStream = File.Create(output))
                    {
                        switch (layout)
                        {
                            case 1: PdfOperations.Create4Up(inStream, outStream); break;
                            case 2: PdfOperations.Create6Up(inStream, outStream); break;
                            case 3: PdfOperations.Create9Up(inStream, outStream); break;
                            default: PdfOperations.Create2Up(inStream, outStream); break;
                        }
                    }
                    Log($"N-up PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error creating N-up: " + ex.Message);
                }
            });
        }

        // ---------------- Extract Tab ----------------
        private void BrowseExtractTextInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) ExtractTextInputPath.Text = path;
        }

        private async void ExtractText_Click(object sender, RoutedEventArgs e)
        {
            string input = ExtractTextInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to extract text from."); return; }

            Log("Extracting text...");
            await Task.Run(() =>
            {
                try
                {
                    string text = PdfOperations.ExtractText(input);
                    Dispatcher.Invoke(() =>
                    {
                        var save = new SaveFileDialog { Filter = "Text files|*.txt|All files|*.*", FileName = "extracted.txt" };
                        if (save.ShowDialog(this) == true)
                        {
                            System.IO.File.WriteAllText(save.FileName, text);
                            Log($"Extracted text saved to {save.FileName}");
                        }
                        else
                        {
                            Log("Extracted text length: " + text.Length);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("Error extracting text: " + ex.Message);
                }
            });
        }

        private async void ExtractStructuredText_Click(object sender, RoutedEventArgs e)
        {
            string input = ExtractTextInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to extract structured text from."); return; }

            Log("Extracting structured text...");
            await Task.Run(() =>
            {
                try
                {
                    var pages = PdfOperations.ExtractStructuredText(input);
                    int totalLines = pages.Sum(p => p.Lines?.Count ?? 0);
                    Log($"Structured text: {pages.Count} pages, {totalLines} text lines.");
                    for (int i = 0; i < Math.Min(3, pages.Count); i++)
                    {
                        var pg = pages[i];
                        Log($"  Page {pg.PageNumber}: {pg.Lines?.Count ?? 0} lines");
                    }
                }
                catch (Exception ex)
                {
                    Log("Error extracting structured text: " + ex.Message);
                }
            });
        }

        private void BrowseSearchInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) SearchInputPath.Text = path;
        }

        private async void SearchText_Click(object sender, RoutedEventArgs e)
        {
            string input = SearchInputPath.Text;
            string query = SearchQuery.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to search."); return; }
            if (string.IsNullOrEmpty(query)) { Log("Enter a search query."); return; }

            bool ignoreCase = SearchIgnoreCase.IsChecked == true;
            Log($"Searching for \"{query}\" (ignoreCase={ignoreCase})...");
            await Task.Run(() =>
            {
                try
                {
                    var hits = PdfOperations.SearchText(input, query, ignoreCase, 60);
                    Log($"Found {hits.Count} hit(s).");
                    foreach (var hit in hits.Take(20))
                        Log($"  Page {hit.PageNumber}: ...{hit.ContextSnippet}...");
                    if (hits.Count > 20)
                        Log($"  (and {hits.Count - 20} more)");
                }
                catch (Exception ex)
                {
                    Log("Error searching text: " + ex.Message);
                }
            });
        }

        private void BrowseExtractImagesInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) ExtractImagesInputPath.Text = path;
        }

        private void BrowseExtractImagesOutputDir_Click(object sender, RoutedEventArgs e)
        {
            var p = BrowseFolder();
            if (!string.IsNullOrEmpty(p)) ExtractImagesOutputDir.Text = p;
        }

        private async void ExtractImages_Click(object sender, RoutedEventArgs e)
        {
            string input = ExtractImagesInputPath.Text;
            string outdir = ExtractImagesOutputDir.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(outdir)) { Log("Select input PDF and output directory."); return; }

            Log("Extracting images...");
            await Task.Run(() =>
            {
                try
                {
                    var list = PdfOperations.ExtractImages(input, outdir);
                    Log($"Extracted {list.Count} images to {outdir}");
                }
                catch (Exception ex)
                {
                    Log("Error extracting images: " + ex.Message);
                }
            });
        }

        private void BrowseTableInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) TableInputPath.Text = path;
        }

        private async void ExtractTables_Click(object sender, RoutedEventArgs e)
        {
            string input = TableInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to extract tables from."); return; }

            Log("Extracting tables...");
            await Task.Run(() =>
            {
                try
                {
                    var tables = PdfOperations.ExtractTables(input);
                    Log($"Found {tables.Count} table(s).");
                    foreach (var tbl in tables.Take(5))
                        Log($"  Table: {tbl.Rows?.Count ?? 0} rows x {tbl.ColumnCount} cols (page {tbl.PageIndex})");
                }
                catch (Exception ex)
                {
                    Log("Error extracting tables: " + ex.Message);
                }
            });
        }

        // ---------------- Rendering Tab ----------------
        private void BrowseRenderInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) RenderInputPath.Text = path;
        }

        private async void RenderPageToPng_Click(object sender, RoutedEventArgs e)
        {
            string input = RenderInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to render."); return; }
            if (!int.TryParse(RenderPageIndex.Text, out int pageIdx) || pageIdx < 0) { Log("Invalid page index."); return; }
            if (!int.TryParse(RenderDpi.Text, out int dpi) || dpi < 1) dpi = 150;
            int pageCount = PdfOperations.GetPageCount(input);
            if (pageIdx >= pageCount) { Log($"Page {pageIdx} is out of range — PDF has {pageCount} page(s) (0-based)."); return; }

            Log($"Rendering page {pageIdx} at {dpi} DPI...");
            await Task.Run(() =>
            {
                try
                {
                    byte[] png = PdfOperations.RenderPageToPng(input, pageIdx, dpi);
                    Dispatcher.Invoke(() =>
                    {
                        var save = new SaveFileDialog { Filter = "PNG files|*.png", FileName = $"page_{pageIdx}.png" };
                        if (save.ShowDialog(this) == true)
                        {
                            File.WriteAllBytes(save.FileName, png);
                            Log($"PNG saved to {save.FileName} ({png.Length:N0} bytes)");
                        }
                        else
                        {
                            Log($"Rendered page {pageIdx}: {png.Length:N0} bytes (not saved)");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("Error rendering page: " + ex.Message);
                }
            });
        }

        private async void RenderAllPagesToPng_Click(object sender, RoutedEventArgs e)
        {
            string input = RenderInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to render."); return; }
            if (!int.TryParse(RenderDpi.Text, out int dpi) || dpi < 1) dpi = 150;

            string outDir = BrowseFolder();
            if (string.IsNullOrEmpty(outDir)) return;

            Log($"Rendering all pages at {dpi} DPI...");
            await Task.Run(() =>
            {
                try
                {
                    byte[][] pages = PdfOperations.RenderAllPagesToPng(input, dpi);
                    for (int i = 0; i < pages.Length; i++)
                        File.WriteAllBytes(Path.Combine(outDir, $"page_{i:D4}.png"), pages[i]);
                    Log($"Rendered {pages.Length} page(s) to {outDir}");
                }
                catch (Exception ex)
                {
                    Log("Error rendering pages: " + ex.Message);
                }
            });
        }

        private async void RenderPageToSvg_Click(object sender, RoutedEventArgs e)
        {
            string input = RenderInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to render."); return; }
            if (!int.TryParse(RenderSvgPageIndex.Text, out int pageIdx) || pageIdx < 0) { Log("Invalid page index."); return; }
            int pageCount = PdfOperations.GetPageCount(input);
            if (pageIdx >= pageCount) { Log($"Page {pageIdx} is out of range — PDF has {pageCount} page(s) (0-based)."); return; }

            Log($"Rendering page {pageIdx} to SVG...");
            await Task.Run(() =>
            {
                try
                {
                    string svg = PdfOperations.RenderPageToSvg(input, pageIdx);
                    Dispatcher.Invoke(() =>
                    {
                        var save = new SaveFileDialog { Filter = "SVG files|*.svg", FileName = $"page_{pageIdx}.svg" };
                        if (save.ShowDialog(this) == true)
                        {
                            File.WriteAllText(save.FileName, svg, System.Text.Encoding.UTF8);
                            Log($"SVG saved to {save.FileName}");
                        }
                        else
                        {
                            Log($"Rendered SVG: {svg.Length:N0} chars (not saved)");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log("Error rendering SVG: " + ex.Message);
                }
            });
        }

        private async void RenderAllPagesToSvg_Click(object sender, RoutedEventArgs e)
        {
            string input = RenderInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to render."); return; }

            string outDir = BrowseFolder();
            if (string.IsNullOrEmpty(outDir)) return;

            Log("Rendering all pages to SVG...");
            await Task.Run(() =>
            {
                try
                {
                    string[] svgs = PdfOperations.RenderAllPagesToSvg(input);
                    for (int i = 0; i < svgs.Length; i++)
                        File.WriteAllText(Path.Combine(outDir, $"page_{i:D4}.svg"), svgs[i], System.Text.Encoding.UTF8);
                    Log($"Rendered {svgs.Length} SVG page(s) to {outDir}");
                }
                catch (Exception ex)
                {
                    Log("Error rendering SVG pages: " + ex.Message);
                }
            });
        }

        private void BrowsePdfToHtmlInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) PdfToHtmlInput.Text = path;
        }

        private async void ConvertPdfToHtml_Click(object sender, RoutedEventArgs e)
        {
            string input = PdfToHtmlInput.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to convert to HTML."); return; }

            string outDir = BrowseFolder();
            if (string.IsNullOrEmpty(outDir)) return;

            Log("Converting PDF to HTML...");
            await Task.Run(() =>
            {
                try
                {
                    PdfHtmlConverter.ConvertToHtmlFiles(input, outDir);
                    Log($"HTML files written to {outDir}");
                }
                catch (Exception ex)
                {
                    Log("Error converting to HTML: " + ex.Message);
                }
            });
        }

        // ---------------- Content & Pages Tab ----------------
        private void BrowseContentInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) ContentInputPath.Text = path;
        }

        private void BrowseWatermarkOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) WatermarkOutputPath.Text = path;
        }

        private async void AddWatermark_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            string output = WatermarkOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            float opacity = 0.3f;
            float.TryParse(WatermarkOpacity.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out opacity);

            var options = new WatermarkOptions
            {
                Text = WatermarkText.Text,
                Opacity = opacity
            };

            Log($"Adding watermark \"{options.Text}\" (opacity={opacity})...");
            await Task.Run(() =>
            {
                try
                {
                    using (var inStream = File.OpenRead(input))
                    using (var outStream = File.Create(output))
                        PdfWatermarker.AddWatermark(inStream, outStream, options);
                    Log($"Watermarked PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error adding watermark: " + ex.Message);
                }
            });
        }

        private void BrowseHeaderFooterOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) HeaderFooterOutputPath.Text = path;
        }

        private async void AddHeaderFooter_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            string output = HeaderFooterOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            var options = new HeaderFooterOptions
            {
                HeaderText = HeaderText.Text,
                FooterText = FooterText.Text
            };

            Log("Adding header/footer...");
            await Task.Run(() =>
            {
                try
                {
                    using (var inStream = File.OpenRead(input))
                    using (var outStream = File.Create(output))
                        PdfOperations.AddHeaderFooter(inStream, outStream, options);
                    Log($"PDF with header/footer written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error adding header/footer: " + ex.Message);
                }
            });
        }

        private void BrowsePageManipOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) PageManipOutputPath.Text = path;
        }

        private async void RotatePage_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            string output = PageManipOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }
            if (!int.TryParse(PageManipIndex.Text, out int pageIdx)) { Log("Invalid page index."); return; }

            int[] angles = { 0, 90, 180, 270 };
            int angle = angles[Math.Max(0, Math.Min(RotationAngle.SelectedIndex, angles.Length - 1))];

            Log($"Rotating page {pageIdx} by {angle} degrees...");
            await Task.Run(() =>
            {
                try
                {
                    using (var inStream = File.OpenRead(input))
                    using (var outStream = File.Create(output))
                        PdfOperations.RotatePage(inStream, outStream, pageIdx, angle);
                    Log($"Rotated PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error rotating page: " + ex.Message);
                }
            });
        }

        private async void DeletePage_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            string output = PageManipOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }
            if (!int.TryParse(PageManipIndex.Text, out int pageIdx)) { Log("Invalid page index."); return; }

            Log($"Deleting page {pageIdx}...");
            await Task.Run(() =>
            {
                try
                {
                    using (var inStream = File.OpenRead(input))
                    using (var outStream = File.Create(output))
                        PdfOperations.DeletePage(inStream, outStream, pageIdx);
                    Log($"PDF with page {pageIdx} removed written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error deleting page: " + ex.Message);
                }
            });
        }

        private async void GetPageCount_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF."); return; }

            await Task.Run(() =>
            {
                try
                {
                    int count = PdfOperations.GetPageCount(input);
                    Log($"Page count: {count}");
                }
                catch (Exception ex)
                {
                    Log("Error getting page count: " + ex.Message);
                }
            });
        }

        private async void GetMetadata_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF."); return; }

            await Task.Run(() =>
            {
                try
                {
                    var meta = PdfOperations.GetMetadata(input);
                    Log($"Metadata: Title=\"{meta.Title}\" Author=\"{meta.Author}\" Subject=\"{meta.Subject}\" Producer=\"{meta.Producer}\" CreationDate={meta.CreationDate}");
                }
                catch (Exception ex)
                {
                    Log("Error getting metadata: " + ex.Message);
                }
            });
        }

        private async void GetBookmarks_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF."); return; }

            await Task.Run(() =>
            {
                try
                {
                    using (var stream = File.OpenRead(input))
                    {
                        var bookmarks = PdfOperations.GetBookmarks(stream);
                        Log($"Bookmarks: {bookmarks.Count}");
                        foreach (var bm in bookmarks.Take(20))
                            Log($"  [{bm.PageNumber}] {bm.Title}");
                        if (bookmarks.Count > 20)
                            Log($"  (and {bookmarks.Count - 20} more)");
                    }
                }
                catch (Exception ex)
                {
                    Log("Error getting bookmarks: " + ex.Message);
                }
            });
        }

        private void BrowseAttachFile_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("All files|*.*");
            if (!string.IsNullOrEmpty(path)) AttachFilePath.Text = path;
        }

        private void BrowseAttachOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) AttachOutputPath.Text = path;
        }

        private async void ListAttachments_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF."); return; }

            await Task.Run(() =>
            {
                try
                {
                    var attachments = PdfOperations.ListAttachments(input);
                    Log($"Attachments: {attachments.Count}");
                    foreach (var att in attachments)
                        Log($"  {att.Filename} ({att.MimeType}, {att.SizeBytes:N0} bytes)");
                }
                catch (Exception ex)
                {
                    Log("Error listing attachments: " + ex.Message);
                }
            });
        }

        private async void AddAttachment_Click(object sender, RoutedEventArgs e)
        {
            string input = ContentInputPath.Text;
            string output = AttachOutputPath.Text;
            string filePath = AttachFilePath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output) || string.IsNullOrEmpty(filePath))
            { Log("Select input PDF, attachment file, and output path."); return; }

            Log($"Adding attachment {Path.GetFileName(filePath)}...");
            await Task.Run(() =>
            {
                try
                {
                    byte[] data = File.ReadAllBytes(filePath);
                    string mimeType = "application/octet-stream";
                    string filename = Path.GetFileName(filePath);
                    PdfOperations.AddAttachment(input, output, filename, data, mimeType);
                    Log($"PDF with attachment written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error adding attachment: " + ex.Message);
                }
            });
        }

        // ---------------- Digital Signing Tab ----------------
        private void BrowseSignInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) SignInputPath.Text = path;
        }

        private void BrowseSignOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) SignOutputPath.Text = path;
        }

        private void BrowseSignCert_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("Certificate files|*.pfx;*.p12|All files|*.*");
            if (!string.IsNullOrEmpty(path)) SignCertPath.Text = path;
        }

        private async void SignPdf_Click(object sender, RoutedEventArgs e)
        {
            string input = SignInputPath.Text;
            string output = SignOutputPath.Text;
            string cert = SignCertPath.Text;
            string certPwd = SignCertPassword.Password;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output) || string.IsNullOrEmpty(cert))
            { Log("Select input PDF, output PDF, and certificate."); return; }

            string reason = SignReason.Text;
            string location = SignLocation.Text;
            string tsaUrl = SignTsaUrl.Text.Trim();

            Log("Signing PDF...");
            await Task.Run(() =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(tsaUrl))
                        PdfSigningOperations.Sign(input, output, cert, certPwd, tsaUrl, false, reason, location);
                    else
                        PdfSigningOperations.Sign(input, output, cert, certPwd, reason, location);
                    Log($"Signed PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error signing PDF: " + ex.Message);
                }
            });
        }

        private void BrowseVerifyInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) VerifyInputPath.Text = path;
        }

        private async void VerifySignatures_Click(object sender, RoutedEventArgs e)
        {
            string input = VerifyInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to verify."); return; }

            Log("Verifying signatures...");
            await Task.Run(() =>
            {
                try
                {
                    using (var stream = File.OpenRead(input))
                    {
                        var results = PdfSigningOperations.VerifySignatures(stream);
                        Log($"Signature verification: {results.Count} signature(s)");
                        foreach (var r in results)
                            Log($"  [{r.FieldName}] Valid={r.IsIntegrityValid} CoversDoc={r.CoversEntireDocument} Signer={r.SignerCommonName}");
                    }
                }
                catch (Exception ex)
                {
                    Log("Error verifying signatures: " + ex.Message);
                }
            });
        }

        private async void GetSignatures_Click(object sender, RoutedEventArgs e)
        {
            string input = VerifyInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF."); return; }

            await Task.Run(() =>
            {
                try
                {
                    var sigs = PdfOperations.GetSignatures(input);
                    Log($"Signature fields: {sigs.Count}");
                    foreach (var s in sigs)
                        Log($"  [{s.FieldName}] Reason={s.Reason} Date={s.SigningTime}");
                }
                catch (Exception ex)
                {
                    Log("Error listing signatures: " + ex.Message);
                }
            });
        }

        private void BrowseTimestampInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) TimestampInputPath.Text = path;
        }

        private void BrowseTimestampOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) TimestampOutputPath.Text = path;
        }

        private async void AddDocumentTimestamp_Click(object sender, RoutedEventArgs e)
        {
            string input = TimestampInputPath.Text;
            string output = TimestampOutputPath.Text;
            string tsaUrl = TimestampTsaUrl.Text.Trim();
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }
            if (string.IsNullOrEmpty(tsaUrl)) { Log("Enter a TSA URL."); return; }

            Log($"Adding document timestamp via {tsaUrl}...");
            await Task.Run(() =>
            {
                try
                {
                    using (var inStream = File.OpenRead(input))
                    using (var outStream = File.Create(output))
                        PdfSigningOperations.AddDocumentTimestamp(inStream, outStream, tsaUrl);
                    Log($"Timestamped PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error adding timestamp: " + ex.Message);
                }
            });
        }

        // ---------------- Blockchain Signature (Ethereum) ----------------
        private void BrowseBlockchainInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) BlockchainInputPath.Text = path;
        }

        private void BrowseBlockchainOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) BlockchainOutputPath.Text = path;
        }

        private void BrowseBlockchainCert_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("Certificate files|*.pfx;*.p12|All files|*.*");
            if (!string.IsNullOrEmpty(path)) BlockchainCertPath.Text = path;
        }

        private async void BlockchainSign_Click(object sender, RoutedEventArgs e)
        {
            string input = BlockchainInputPath.Text;
            string output = BlockchainOutputPath.Text;
            string certPath = BlockchainCertPath.Text;
            string certPwd = BlockchainCertPassword.Password;
            string rpcUrl = BlockchainRpcUrl.Text;
            string privateKey = BlockchainPrivateKey.Password;

            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output))
            { Log("Select input and output PDF paths."); return; }
            if (string.IsNullOrEmpty(certPath))
            { Log("Select a certificate (.pfx) file."); return; }
            if (string.IsNullOrEmpty(rpcUrl) || string.IsNullOrEmpty(privateKey))
            { Log("Provide Ethereum RPC URL and private key."); return; }

            int chainId = 11155111;
            int.TryParse(BlockchainChainId.Text, out chainId);

            BlockchainStatus.Text = "Signing and recording on blockchain...";
            Log($"Blockchain sign: chain={chainId}, rpc={rpcUrl}");

            try
            {
                var sigParams = new Tetitulz.Pdf.Signing.Models.SignatureParameters
                {
                    CertificatePath = certPath,
                    CertificatePassword = certPwd
                };
                var blockchainParams = new BlockchainParameters
                {
                    RpcUrl = rpcUrl,
                    PrivateKey = privateKey,
                    ChainId = chainId
                };
                var signer = new PdfBlockchainSigner();
                using (var inStream = System.IO.File.OpenRead(input))
                using (var outStream = System.IO.File.Create(output))
                {
                    await signer.SignAsync(inStream, outStream, sigParams, blockchainParams);
                }
                BlockchainStatus.Text = "Done! Signed and recorded on blockchain.";
                Log($"Blockchain-signed PDF written to {output}");
            }
            catch (Exception ex)
            {
                BlockchainStatus.Text = "Error: " + ex.Message;
                Log("Blockchain sign error: " + ex.Message);
            }
        }

        private async void BlockchainVerify_Click(object sender, RoutedEventArgs e)
        {
            string input = BlockchainInputPath.Text;
            string rpcUrl = BlockchainRpcUrl.Text;

            if (string.IsNullOrEmpty(input))
            { Log("Select an input PDF to verify."); return; }
            if (string.IsNullOrEmpty(rpcUrl))
            { Log("Provide Ethereum RPC URL for verification."); return; }

            BlockchainStatus.Text = "Verifying blockchain proof...";
            Log("Verifying blockchain proof...");

            try
            {
                var verifier = new PdfBlockchainVerifier();
                var result = await verifier.VerifyAsync(input, new BlockchainParameters { RpcUrl = rpcUrl });
                if (result.IsValid)
                {
                    BlockchainStatus.Text = "Valid! Document hash matches on-chain data.";
                    Log($"Blockchain verification: VALID (tx={result.TransactionHash}, block={result.BlockNumber})");
                }
                else
                {
                    BlockchainStatus.Text = "Invalid: " + (result.Error ?? "Hash mismatch");
                    Log($"Blockchain verification: INVALID - {result.Error}");
                }
            }
            catch (Exception ex)
            {
                BlockchainStatus.Text = "Error: " + ex.Message;
                Log("Blockchain verify error: " + ex.Message);
            }
        }

        // ---------------- Validation Tab ----------------
        private void BrowseValidateInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) ValidateInputPath.Text = path;
        }

        private async void ValidatePdfA_Click(object sender, RoutedEventArgs e)
        {
            string input = ValidateInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to validate."); return; }
            var pdfAPart = PdfAConformance.SelectedIndex switch { 1 => PdfAConformancePart.Part2, 2 => PdfAConformancePart.Part3, _ => PdfAConformancePart.Part1 };
            int levelNum = (int)pdfAPart;

            Log($"Validating PDF/A-{levelNum}...");
            await Task.Run(() =>
            {
                try
                {
                    var result = PdfAValidator.Validate(input, pdfAPart);
                    Log($"PDF/A-{levelNum} conformant: {result.IsConformant}");
                    foreach (var v in result.Violations.Take(10))
                        Log($"  Violation: {v}");
                    if (result.Violations.Count > 10)
                        Log($"  (and {result.Violations.Count - 10} more violations)");
                }
                catch (Exception ex)
                {
                    Log("Error validating PDF/A: " + ex.Message);
                }
            });
        }

        private void BrowsePdfAOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) PdfAOutputPath.Text = path;
        }

        private async void ConvertToPdfA_Click(object sender, RoutedEventArgs e)
        {
            string input = ValidateInputPath.Text;
            string output = PdfAOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            int conformanceIdx = PdfAConformance.SelectedIndex;
            if (conformanceIdx != 0)
            {
                string[] levels = { "1b", "2b", "3b" };
                Log($"PDF/A-{levels[conformanceIdx]} conversion is not yet supported — only PDF/A-1b is available. Aborting.");
                return;
            }

            Log("Converting to PDF/A-1b...");
            SetBusy(true);
            await Task.Run(() =>
            {
                try
                {
                    using (var inStream = File.OpenRead(input))
                    using (var outStream = File.Create(output))
                        PdfAConverter.ConvertToPdfA1b(inStream, outStream);
                    Log($"PDF/A-1b written to {output}");
                }
                catch (Exception ex)
                {
                    Log($"Error converting to PDF/A: {ex.Message}");
                }
            });
            SetBusy(false);
        }

        private async void ValidatePdfUa_Click(object sender, RoutedEventArgs e)
        {
            string input = ValidateInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to validate."); return; }

            Log("Validating PDF/UA...");
            await Task.Run(() =>
            {
                try
                {
                    var result = PdfUaChecker.Validate(input);
                    Log($"PDF/UA conforming: {result.IsConforming}");
                    foreach (var v in result.Violations.Take(10))
                        Log($"  Violation: {v}");
                    if (result.Violations.Count > 10)
                        Log($"  (and {result.Violations.Count - 10} more)");
                }
                catch (Exception ex)
                {
                    Log("Error validating PDF/UA: " + ex.Message);
                }
            });
        }

        private void BrowsePdfUaOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) PdfUaOutputPath.Text = path;
        }

        private async void FixAccessibility_Click(object sender, RoutedEventArgs e)
        {
            string input = ValidateInputPath.Text;
            string output = PdfUaOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            Log("Fixing accessibility...");
            await Task.Run(() =>
            {
                try
                {
                    using (var inStream = File.OpenRead(input))
                    using (var outStream = File.Create(output))
                    {
                        var result = PdfAccessibilityFixer.Fix(inStream, outStream);
                        Log($"Accessibility fixed: {result.FixesApplied.Count} fix(es) applied. Output: {output}");
                    }
                }
                catch (Exception ex)
                {
                    Log("Error fixing accessibility: " + ex.Message);
                }
            });
        }

        private async void ValidatePdfX_Click(object sender, RoutedEventArgs e)
        {
            string input = ValidateInputPath.Text;
            if (string.IsNullOrEmpty(input)) { Log("Select a PDF to validate."); return; }
            int stdIdx = PdfXStandard.SelectedIndex;
            var level = stdIdx switch
            {
                1 => PdfXConformanceLevel.X3,
                2 => PdfXConformanceLevel.X4,
                _ => PdfXConformanceLevel.X1a
            };

            Log($"Validating PDF/X ({level})...");
            await Task.Run(() =>
            {
                try
                {
                    var result = PdfXValidator.Validate(input, level);
                    Log($"PDF/X conforming: {result.IsConforming}");
                    foreach (var v in result.Violations.Take(10))
                        Log($"  Violation: {v}");
                    if (result.Violations.Count > 10)
                        Log($"  (and {result.Violations.Count - 10} more)");
                }
                catch (Exception ex)
                {
                    Log("Error validating PDF/X: " + ex.Message);
                }
            });
        }

        private void BrowseRepairOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) RepairOutputPath.Text = path;
        }

        private async void RepairPdf_Click(object sender, RoutedEventArgs e)
        {
            string input = ValidateInputPath.Text;
            string output = RepairOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            Log("Repairing PDF...");
            SetBusy(true);
            await Task.Run(() =>
            {
                try
                {
                    var result = PdfRepair.Repair(input, output);
                    Log($"Repair done. Clean={result.IsClean}, errors fixed: {result.Errors?.Count ?? 0}.");
                    if (result.Errors != null)
                        foreach (var err in result.Errors.Take(10))
                            Log($"  Fixed: {err}");
                }
                catch (Exception ex)
                {
                    Log($"Error repairing PDF: {ex.Message}");
                }
            });
            SetBusy(false);
        }

        // ---------------- Security Tab ----------------
        private void BrowseEncryptInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) EncryptInputPath.Text = path;
        }

        private void BrowseEncryptOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) EncryptOutputPath.Text = path;
        }

        private async void EncryptPdf_Click(object sender, RoutedEventArgs e)
        {
            string input = EncryptInputPath.Text;
            string output = EncryptOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            var userPwd = EncryptUserPassword.Password;
            var ownerPwd = EncryptOwnerPassword.Password;
            if (string.IsNullOrEmpty(ownerPwd)) ownerPwd = userPwd;

            Log("Encrypting PDF...");
            await Task.Run(() =>
            {
                try
                {
                    PdfEncryptionOperations.SetPassword(input, output, userPwd, ownerPwd);
                    Log($"Encrypted PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error encrypting PDF: " + ex.Message);
                }
            });
        }

        private void BrowseDecryptInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) DecryptInputPath.Text = path;
        }

        private void BrowseDecryptOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) DecryptOutputPath.Text = path;
        }

        private async void DecryptPdf_Click(object sender, RoutedEventArgs e)
        {
            string input = DecryptInputPath.Text;
            string output = DecryptOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            var pwd = DecryptPassword.Password;
            Log("Decrypting PDF...");
            await Task.Run(() =>
            {
                try
                {
                    PdfEncryptionOperations.RemovePassword(input, output, pwd);
                    Log($"Decrypted PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log("Error decrypting PDF: " + ex.Message);
                }
            });
        }

        private void BrowseSanitizeInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) SanitizeInputPath.Text = path;
        }

        private void BrowseSanitizeOutput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseSaveFile("PDF files|*.pdf");
            if (!string.IsNullOrEmpty(path)) SanitizeOutputPath.Text = path;
        }

        private async void SanitizePdf_Click(object sender, RoutedEventArgs e)
        {
            string input = SanitizeInputPath.Text;
            string output = SanitizeOutputPath.Text;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output)) { Log("Select input and output paths."); return; }

            Log("Sanitizing PDF...");
            SetBusy(true);
            await Task.Run(() =>
            {
                try
                {
                    PdfSanitizer.Sanitize(input, output);
                    Log($"Sanitized PDF written to {output}");
                }
                catch (Exception ex)
                {
                    Log($"Error sanitizing PDF: {ex.Message}");
                }
            });
            SetBusy(false);
        }

        // ---------------- Brute Force Password Finder ----------------
        private CancellationTokenSource _bruteForceCts;
        private ManualResetEventSlim _bruteForcePause = new ManualResetEventSlim(true);
        private bool _bruteForcePaused;

        private void BrowseBruteForceInput_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseOpenFile("PDF files|*.pdf|All files|*.*");
            if (!string.IsNullOrEmpty(path)) BruteForceInputPath.Text = path;
        }

        private async void BruteForceStart_Click(object sender, RoutedEventArgs e)
        {
            string inputPath = BruteForceInputPath.Text;
            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
            {
                Log("Select an encrypted PDF file."); return;
            }

            string charset = BruteForceCharset.Text;
            if (string.IsNullOrEmpty(charset)) { Log("Character set cannot be empty."); return; }

            if (!int.TryParse(BruteForceMinLen.Text, out int minLen) || minLen < 1)
            { Log("Invalid min length."); return; }
            if (!int.TryParse(BruteForceMaxLen.Text, out int maxLen) || maxLen < minLen)
            { Log("Invalid max length (must be >= min)."); return; }
            if (!int.TryParse(BruteForceThreads.Text, out int threadCount) || threadCount < 1)
            { Log("Invalid thread count."); return; }

            EncryptionInfo encInfo;
            try
            {
                encInfo = PdfDecryptor.GetEncryptionInfo(inputPath);
            }
            catch (Exception ex)
            {
                Log("Failed to read encryption info: " + ex.Message);
                return;
            }

            long total = 0;
            for (int len = minLen; len <= maxLen; len++)
            {
                long combos = 1;
                for (int i = 0; i < len; i++) combos *= charset.Length;
                total += combos;
            }

            _bruteForceCts = new CancellationTokenSource();
            _bruteForcePause.Set();
            _bruteForcePaused = false;

            BruteForceStartBtn.IsEnabled = false;
            BruteForcePauseBtn.IsEnabled = true;
            BruteForcePauseBtn.Content = "Pause";
            BruteForceStopBtn.IsEnabled = true;
            BruteForceProgress.Maximum = total;
            BruteForceProgress.Value = 0;
            BruteForceStatus.Text = $"Searching... 0 / {total:N0}";

            Log($"Brute force started: charset={charset.Length} chars, lengths {minLen}-{maxLen}, {total:N0} combinations, {threadCount} threads");

            var token = _bruteForceCts.Token;
            string foundPassword = null;
            long tested = 0;

            try
            {
                foundPassword = await Task.Run(() =>
                {
                    string result = null;
                    char[] chars = charset.ToCharArray();

                    for (int len = minLen; len <= maxLen && result == null; len++)
                    {
                        if (token.IsCancellationRequested) break;

                        Parallel.For(0, chars.Length, new ParallelOptions
                        {
                            MaxDegreeOfParallelism = threadCount,
                            CancellationToken = token
                        },
                        (firstIdx, loopState) =>
                        {
                            char[] candidate = new char[len];
                            candidate[0] = chars[firstIdx];

                            for (int p = 1; p < len; p++) candidate[p] = chars[0];

                            while (true)
                            {
                                _bruteForcePause.Wait(token);
                                if (token.IsCancellationRequested || loopState.IsStopped) return;

                                string pwd = new string(candidate);
                                if (PdfDecryptor.TryPassword(encInfo, pwd))
                                {
                                    Interlocked.Exchange(ref result, pwd);
                                    loopState.Stop();
                                    return;
                                }

                                long count = Interlocked.Increment(ref tested);
                                if (count % 1000 == 0)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        BruteForceProgress.Value = count;
                                        BruteForceStatus.Text = $"Searching... {count:N0} / {total:N0}";
                                    }));
                                }

                                bool carry = true;
                                for (int p = len - 1; p >= 1 && carry; p--)
                                {
                                    int idx = Array.IndexOf(chars, candidate[p]) + 1;
                                    if (idx < chars.Length)
                                    {
                                        candidate[p] = chars[idx];
                                        carry = false;
                                    }
                                    else
                                    {
                                        candidate[p] = chars[0];
                                    }
                                }
                                if (carry) break;
                            }
                        });
                    }

                    return result;
                }, token);
            }
            catch (OperationCanceledException)
            {
                // User stopped
            }

            BruteForceStartBtn.IsEnabled = true;
            BruteForcePauseBtn.IsEnabled = false;
            BruteForceStopBtn.IsEnabled = false;

            if (foundPassword != null)
            {
                BruteForceStatus.Text = $"Password found after {tested:N0} attempts — copied to Decrypt field.";
                BruteForceProgress.Value = BruteForceProgress.Maximum;
                DecryptPassword.Password = foundPassword;
                Log($"Password found after {tested:N0} attempts. Populated into Decrypt field.");
            }
            else if (_bruteForceCts.IsCancellationRequested)
            {
                BruteForceStatus.Text = $"Stopped after {tested:N0} attempts";
                Log($"Brute force stopped by user after {tested:N0} attempts.");
            }
            else
            {
                BruteForceStatus.Text = "Password not found";
                BruteForceProgress.Value = BruteForceProgress.Maximum;
                Log($"Password not found after {tested:N0} attempts.");
            }
        }

        private void BruteForcePause_Click(object sender, RoutedEventArgs e)
        {
            if (_bruteForcePaused)
            {
                _bruteForcePause.Set();
                _bruteForcePaused = false;
                BruteForcePauseBtn.Content = "Pause";
                BruteForceStatus.Text = BruteForceStatus.Text.Replace("Paused", "Searching...");
                Log("Brute force resumed.");
            }
            else
            {
                _bruteForcePause.Reset();
                _bruteForcePaused = true;
                BruteForcePauseBtn.Content = "Resume";
                BruteForceStatus.Text = BruteForceStatus.Text.Replace("Searching...", "Paused");
                Log("Brute force paused.");
            }
        }

        private void BruteForceStop_Click(object sender, RoutedEventArgs e)
        {
            _bruteForceCts?.Cancel();
            _bruteForcePause.Set();
            Log("Stopping brute force...");
        }
    }
}
