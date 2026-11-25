// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>When <see cref="IsraeliInvoiceExtractor"/> recognises a page optically.</summary>
    public enum InvoiceOcrMode
    {
        /// <summary>Never: a page without readable text contributes nothing.</summary>
        Never,

        /// <summary>
        /// A page with no text is rendered and recognised — when an <c>IOcrWordPlugin</c> is
        /// registered. The engine's language is the plugin's: register Tesseract with
        /// <c>"heb+eng"</c> for Israeli invoices. A page whose text is Hebrew in a legacy 8-bit
        /// encoding is decoded as Windows-1255 instead, and reported.
        /// </summary>
        WhenNeeded,
    }

    /// <summary>Options for <see cref="IsraeliInvoiceExtractor"/>.</summary>
    public sealed class IsraeliInvoiceOptions
    {
        /// <summary>Recorded as the invoice's <see cref="IsraeliInvoice.Source"/>.</summary>
        public string Source { get; set; }

        /// <summary>When to recognise a page optically. Default <see cref="InvoiceOcrMode.WhenNeeded"/>.</summary>
        public InvoiceOcrMode Ocr { get; set; } = InvoiceOcrMode.WhenNeeded;

        /// <summary>The resolution a page is rendered at for recognition. Default 300.</summary>
        public int OcrDpi { get; set; } = 300;

        /// <summary>Read the item table. Default <c>true</c>.</summary>
        public bool ExtractLineItems { get; set; } = true;

        /// <summary>
        /// Read a structured invoice embedded in the PDF (Factur-X, ZUGFeRD, XRechnung, UBL) and
        /// prefer its values to the page's. Default <c>true</c>.
        /// </summary>
        public bool ReadEmbeddedInvoice { get; set; } = true;

        /// <summary>The validation's tolerances; <c>null</c> for the defaults.</summary>
        public IsraeliInvoiceValidationOptions Validation { get; set; }
    }

    /// <summary>
    /// Reads an Israeli invoice — Hebrew, right to left — into an <see cref="IsraeliInvoice"/> and
    /// validates it: document type, number, date, supplier and customer numbers, allocation
    /// number, subtotal, exempt part, VAT and total, and the item table.
    /// </summary>
    /// <remarks>
    /// <para><b>A preset over the general tools, not a new engine.</b> Fields are found by
    /// <see cref="PdfFieldExtractor"/> with the labels in <see cref="CreateQueries()"/>; the item table
    /// by <see cref="PdfTableExtractor"/>; values by <see cref="InvoiceValueParser"/>. The label
    /// lists are the wordings vendors' published samples and the Tax Authority's specification
    /// print, not proven against real invoice files (GAPS.md §4a), and every one can be replaced:
    /// build the queries, change them, and use <see cref="PdfFieldExtractor"/> directly.</para>
    /// <para><b>A value the field cannot use is passed over</b> for the next match: an invoice
    /// number with no digit, a date that is no date, a total that is no amount — or an amount with
    /// words after it, which a sentence holds rather than a field ("לתשלום הפיצוי בתוך 30 ימים").
    /// Of the totals that are amounts, one that the subtotal and the VAT add up to wins — a paid
    /// invoice's "balance due 0.00" is not its total.</para>
    /// <para><b>A row of headings over a row of values</b> — <c>מספר מסמך | תאריך | לכבוד</c> — is
    /// read a value under each heading; a heading over a column of such values is a table's, and
    /// gives none.</para>
    /// <para><b>The result is checked, not trusted.</b> <see cref="IsraeliInvoiceValidator"/> runs
    /// on every result: when the subtotal, the VAT and the total agree with each other and with
    /// the rate on the invoice's date, the reading is very likely right; when they do not, the
    /// findings say so with the numbers.</para>
    /// <para><b>Identification numbers are told apart by position and cue, which is a
    /// heuristic.</b> Both the supplier's and the customer's numbers carry the same labels
    /// (<c>ח.פ.</c>, <c>עוסק מורשה</c>). The first number on, or within five lines below, a
    /// customer cue (<c>לכבוד</c>, <c>לקוח</c>, "customer", "bill to") is the customer's — the
    /// first such number; there is one customer — and the first other number is the supplier's.
    /// With no cue, a second number that passes the check digit is taken as the customer's. A
    /// number one Latin prefix away from its digits (<c>DE123456789</c>) is kept whole, as the
    /// foreign number it is; an Israeli one prefixed <c>IL</c> is read as its digits.</para>
    /// <para><b>Encrypted invoices</b> are decrypted through the registered decryptor — with the
    /// empty password unless one is given, which opens the permissions-only protection invoicing
    /// software commonly applies. With no decryptor registered an encrypted invoice is refused
    /// with <see cref="PdfEncryptedDocumentException"/>: its text is ciphertext.</para>
    /// </remarks>
    public static class IsraeliInvoiceExtractor
    {
        /// <summary>The <see cref="FieldQuery.Key"/> of the invoice-number query.</summary>
        public const string InvoiceNumberKey = "invoiceNumber";
        /// <summary>
        /// The <see cref="FieldQuery.Key"/> of the query for a number printed straight after the
        /// document's title with no label of its own (<c>חשבונית מס 2146</c>) — read only when the
        /// invoice-number query finds none, and only beside the title.
        /// </summary>
        public const string TitleNumberKey = "titleNumber";
        /// <summary>The <see cref="FieldQuery.Key"/> of the date query.</summary>
        public const string DateKey = "date";
        /// <summary>The <see cref="FieldQuery.Key"/> of the subtotal (before VAT) query.</summary>
        public const string SubtotalKey = "subtotal";
        /// <summary>The <see cref="FieldQuery.Key"/> of the exempt or zero-rated amount's query.</summary>
        public const string ExemptKey = "exempt";
        /// <summary>The <see cref="FieldQuery.Key"/> of the VAT query.</summary>
        public const string VatKey = "vat";
        /// <summary>The <see cref="FieldQuery.Key"/> of the total query.</summary>
        public const string TotalKey = "total";
        /// <summary>
        /// The <see cref="FieldQuery.Key"/> of the amount-paid query: a receipt's total when it
        /// prints no other (<c>סה"כ שולם</c>, which on an invoice is the part paid so far).
        /// </summary>
        public const string PaidKey = "paid";
        /// <summary>The <see cref="FieldQuery.Key"/> of the allocation-number query.</summary>
        public const string AllocationNumberKey = "allocationNumber";
        /// <summary>The <see cref="FieldQuery.Key"/> of the identification-number query (supplier and customer alike).</summary>
        public const string IdentifierKey = "identifier";

        /// <summary>
        /// Why the last extraction on this thread read nothing from a page, or stopped short of
        /// reading everything; <c>null</c> when it read every page through.
        /// <c>[ThreadStatic]</c>: read it on the thread that ran the extraction.
        /// </summary>
        public static string LastError => _lastError;

        [ThreadStatic] private static string _lastError;

        // ----------------------------------------------------------------
        //  The label preset
        // ----------------------------------------------------------------

        // Document titles, most specific first: a longer title contains a shorter one.
        private static readonly (IsraeliDocumentType Type, string[] Phrases)[] Titles =
        {
            (IsraeliDocumentType.TaxInvoiceReceipt,
                new[] { "חשבונית מס קבלה", "חשבונית מס/קבלה", "חשבונית מס וקבלה", "Tax Invoice Receipt", "Tax Invoice and Receipt" }),
            (IsraeliDocumentType.CreditInvoice,
                new[] { "חשבונית מס זיכוי", "חשבונית זיכוי", "Credit Invoice", "Credit Note" }),
            (IsraeliDocumentType.AgentInvoice, new[] { "חשבונית מס סוכן", "חשבונית סוכן" }),
            (IsraeliDocumentType.PeriodicTaxInvoice,
                new[] { "חשבונית מס ריכוז", "חשבונית ריכוז", "חשבונית מס תקופתית", "חשבונית תקופתית" }),
            (IsraeliDocumentType.Proforma,
                new[] { "חשבון עסקה פרופורמה", "חשבון עסקה/פרופורמה", "חשבונית פרופורמה", "פרופורמה",
                        "Proforma Invoice", "Pro Forma Invoice", "Proforma" }),
            (IsraeliDocumentType.TaxInvoice, new[] { "חשבונית מס", "Tax Invoice" }),
            (IsraeliDocumentType.TransactionInvoice, new[] { "חשבון עסקה", "חשבונית עסקה" }),
            (IsraeliDocumentType.DonationReceipt,
                new[] { "קבלה על תרומה", "קבלה על תרומות", "קבלה לתרומה", "Donation Receipt" }),
        };

        // The same titles as runs of words — compared with a line's words, never as substrings.
        private static readonly (IsraeliDocumentType Type, List<string[]> Phrases)[] TitleWords =
            Titles.Select(t => (t.Type, t.Phrases.Select(PhraseWords).ToList())).ToArray();

        // A receipt word standing as a title, when no title phrase was found, makes a receipt.
        private static readonly HashSet<string> ReceiptWordKeys =
            new HashSet<string>(new[] { "קבלה", "receipt" }.Select(InvoiceValueParser.LabelKey), StringComparer.Ordinal);

        // The words a document's number follows after its title — "חשבונית מס מס' 2146", "Tax
        // Invoice No 2146": the invoice number's labels are built with them, and a title they
        // follow still stands.
        private static readonly string[] HebrewNumberWords = { "מס'", "מספר" };
        private static readonly string[] LatinNumberWords = { "No", "Number", "#" };
        private static readonly HashSet<string> NumberWordKeys = new HashSet<string>(
            HebrewNumberWords.Concat(LatinNumberWords).Select(InvoiceValueParser.LabelKey), StringComparer.Ordinal);

        // A copy's marks, which may stand around a title: "מקור", "העתק נאמן למקור", "(העתק)".
        private static readonly HashSet<string> CopyMarkKeys = new HashSet<string>(
            new[] { "מקור", "העתק", "נאמן", "למקור", "Original", "Copy" }.Select(InvoiceValueParser.LabelKey),
            StringComparer.Ordinal);

        // The most words with letters a title's block holds after it, number words and copy marks
        // aside: "חשבונית מס לחודש ינואר 2026" is a title, "חשבונית מס תישלח אליך בדואר" a sentence.
        private const int MaxWordsAfterTitle = 2;

        // Customer cues: a word equal to one of these (colon dropped), or a line containing a phrase.
        private static readonly string[] CustomerCueWords = { "לכבוד", "לקוח", "customer", "client" };
        private static readonly string[] CustomerCuePhrases = { "שם הלקוח", "פרטי לקוח", "פרטי הלקוח", "bill to", "billed to" };
        // Lines that name the customer without starting the customer's block.
        private static readonly string[] NotCustomerCuePhrases =
        {
            "חתימת לקוח", "חתימת הלקוח", "שירות לקוחות", "customer service", "customer signature", "client signature",
        };

        // The note a tax invoice issued without an allocation number, after a refusal, carries.
        private static readonly string InputVatNotDeductibleKey = InvoiceValueParser.LabelKey("אין לנכות מס תשומות");

        // UN/CEFACT document type codes (UNTDID 1001) of credit notes, which state amounts positive.
        private static readonly HashSet<string> CreditNoteTypeCodes =
            new HashSet<string>(StringComparer.Ordinal) { "381", "261", "262", "296", "308", "396", "420", "458", "532" };

        /// <summary>
        /// The queries the extractor asks, freshly built — change them and run
        /// <see cref="PdfFieldExtractor"/> with them to adapt the preset to a producer.
        /// </summary>
        /// <remarks>
        /// <para>The invoice-number labels include each title followed by a number word
        /// (<c>חשבונית מס זיכוי מס'</c>), built from the titles the document type is read from, so
        /// the two lists cannot drift apart. A reference to another invoice ("עבור חשבונית מס'
        /// 1200", on a credit invoice) is excluded.</para>
        /// <para>The subtotal's labels include the sum taxed at each rate the VAT has had
        /// (<c>סה"כ סכומים ב-17% מע"מ</c>), built from <see cref="IsraeliTaxRules.DefaultVatRates"/> the
        /// same way; the rate phrase is excluded from the VAT query, whose word it holds. A rate the
        /// table never held prints a phrase no label matches — <see cref="CreateQueries(IReadOnlyList{IsraeliDatedFigure})"/>
        /// takes a table that holds it.</para>
        /// </remarks>
        public static IReadOnlyList<FieldQuery> CreateQueries() => CreateQueries(null);

        /// <summary>
        /// The queries, their rate-naming labels built from <paramref name="vatRates"/> — a table the
        /// law changed after this release (<see cref="IsraeliInvoiceValidationOptions.VatRates"/>);
        /// <see cref="IsraeliTaxRules.DefaultVatRates"/> when <c>null</c> or empty.
        /// </summary>
        public static IReadOnlyList<FieldQuery> CreateQueries(IReadOnlyList<IsraeliDatedFigure> vatRates)
        {
            var numberLabels = new List<string>
            {
                "מס' חשבונית", "מספר חשבונית", "חשבונית מס'", "חשבונית מספר",
                "חשבונית מס מס'", "חשבונית מס/קבלה מס'", "חשבון עסקה מס'", "קבלה מס'",
                "מס' מסמך", "מספר מסמך", "מס' קבלה", "מספר קבלה",
                "Invoice No", "Invoice Number", "Invoice #", "Receipt No",
            };
            var titleLabels = new List<string>();
            foreach (var (_, phrases) in Titles)
                foreach (string phrase in phrases)
                {
                    titleLabels.Add(phrase);
                    foreach (string word in phrase.Any(IsHebrewLetter) ? HebrewNumberWords : LatinNumberWords)
                        numberLabels.Add(phrase + " " + word);
                }
            numberLabels = numberLabels.Distinct().ToList();

            var numberExcludes = new List<string> { "הקצאה", "הזמנה", "עוסק", "Order" };
            foreach (string label in numberLabels.Where(l => l.StartsWith("חשבונ", StringComparison.Ordinal)))
            {
                numberExcludes.Add("עבור " + label);
                numberExcludes.Add("בגין " + label);
            }

            // The sum taxed at a rate names the rate: "סה"כ סכומים ב-17% מע"מ".
            // Printed bare or with two decimals: "ב-18% מע"מ", "ב-18.00% מע"מ".
            var atRate = IsraeliTaxRules.StandardVatRates(vatRates)
                .SelectMany(r => new[] { r.ToString("0.##", CultureInfo.InvariantCulture), r.ToString("0.00", CultureInfo.InvariantCulture) })
                .Distinct()
                .Select(r => "ב-" + r + "% מע\"מ")
                .ToList();
            var subtotalLabels = new List<string>
            {
                "סה\"כ לפני מע\"מ", "סכום לפני מע\"מ", "סה\"כ ללא מע\"מ", "סכום ללא מע\"מ",
                "סה\"כ חייב במע\"מ", "סה\"כ חייב מע\"מ", "סכום חייב במע\"מ", "חייב במע\"מ",
                "סה\"כ חייבים במע\"מ", "סכומים חייבים במע\"מ",
                "סה\"כ לאחר הנחה", "לאחר הנחה", "סכום ביניים", "סה\"כ ביניים",
                "Subtotal", "Sub Total", "Total before VAT", "Total excl. VAT", "Amount before VAT",
            };
            subtotalLabels.AddRange(atRate.Select(phrase => "סה\"כ סכומים " + phrase));
            var vatExcludes = new List<string>
            {
                "לפני מע\"מ", "ללא מע\"מ", "כולל מע\"מ", "אחרי מע\"מ", "חייב במע\"מ", "חייב מע\"מ",
                "חייבים במע\"מ", "פטור ממע\"מ",
                // A rate's heading names the rate, not the VAT: "אחוז מע"מ", "שעור מע"מ".
                "אחוז מע\"מ", "שיעור מע\"מ", "שעור מע\"מ", "פטור מע\"מ", "before VAT", "incl. VAT", "including VAT", "inc. VAT",
                "excl. VAT", "VAT No", "VAT Reg", "VAT ID", "VAT Exempt",
                // A price "plus VAT" names no VAT: "40 ₪ + מע"מ". Written in words, "בתוספת מע"מ",
                // the price has words after it, which no amount a value is has.
                "+ מע\"מ",
            };
            vatExcludes.AddRange(atRate);

            return new List<FieldQuery>
            {
                new FieldQuery(InvoiceNumberKey, numberLabels.ToArray())
                {
                    ExcludeLabels = numberExcludes,
                    WholeWord = true,
                },
                new FieldQuery(TitleNumberKey, titleLabels.ToArray())
                {
                    ExcludeLabels = numberExcludes,
                    WholeWord = true,
                },
                new FieldQuery(DateKey,
                    "תאריך", "תאריך הפקה", "תאריך חשבונית", "תאריך מסמך", "Date", "Invoice Date", "Issue Date")
                {
                    ExcludeLabels = new[]
                    {
                        "תאריך פירעון", "תאריך תשלום", "תאריך ערך", "תאריך אספקה", "תאריך משלוח",
                        "Due Date", "Payment Date", "Delivery Date",
                    },
                    WholeWord = true,
                },
                new FieldQuery(SubtotalKey, subtotalLabels.ToArray())
                {
                    Occurrence = FieldOccurrence.Last,
                    ValueKind = FieldValueKind.Amount,
                    ExcludeLabels = new[] { "לתשלום לאחר הנחה", "כולל מע\"מ לאחר הנחה" },
                },
                new FieldQuery(ExemptKey,
                    "סה\"כ פטור ממע\"מ", "סכום פטור ממע\"מ", "פטור ממע\"מ", "סה\"כ פטור", "סה\"כ בשיעור אפס",
                    "Exempt", "VAT Exempt", "Zero-rated", "Zero rated")
                {
                    Occurrence = FieldOccurrence.Last,
                    ValueKind = FieldValueKind.Amount,
                    WholeWord = true,
                },
                new FieldQuery(VatKey, "מע\"מ", "סה\"כ מע\"מ", "מע\"מ נגבה", "המע\"מ", "סכום המע\"מ", "VAT")
                {
                    Occurrence = FieldOccurrence.Last,
                    ValueKind = FieldValueKind.Amount,
                    WholeWord = true,
                    ExcludeLabels = vatExcludes,
                },
                new FieldQuery(TotalKey,
                    "סה\"כ לתשלום", "סכום לתשלום", "לתשלום", "סה\"כ כולל מע\"מ", "סה\"כ אחרי מע\"מ",
                    "סה\"כ לתשלום כולל מע\"מ", "סה\"כ כולל מע\"מ לתשלום", "סכום כולל מע\"מ",
                    "סכום לתשלום כולל מע\"מ", "סה\"כ חשבונית", "כולל מע\"מ", "סה\"כ",
                    "Total", "Grand Total", "Total Due", "Amount Due", "Total to pay",
                    "Total incl. VAT", "Total including VAT", "Total inc. VAT", "Amount incl. VAT")
                {
                    Occurrence = FieldOccurrence.Last,
                    ValueKind = FieldValueKind.Amount,
                    ExcludeLabels = new[]
                    {
                        "לפני מע\"מ", "ללא מע\"מ", "סה\"כ מע\"מ", "חייב במע\"מ", "חייב מע\"מ", "חייבים במע\"מ", "להעברה",
                        "מעמוד קודם", "סה\"כ פטור", "סה\"כ בשיעור אפס", "סה\"כ לאחר הנחה", "סה\"כ פריטים",
                        "סה\"כ הנחה", "סה\"כ שולם", "סה\"כ תשלומים", "יתרה לתשלום",
                        "Subtotal", "Sub Total", "before VAT", "excl. VAT", "Total VAT",
                    },
                },
                new FieldQuery(PaidKey,
                    "סה\"כ שולם", "סה\"כ ששולם", "סכום ששולם", "סה\"כ התקבל", "סכום שהתקבל",
                    "Total Paid", "Amount Paid", "Amount Received")
                {
                    Occurrence = FieldOccurrence.Last,
                    ValueKind = FieldValueKind.Amount,
                },
                new FieldQuery(AllocationNumberKey,
                    "מספר הקצאה", "מס' הקצאה", "מס הקצאה", "הקצאה מספר", "הקצאה",
                    "מספר אישור הקצאה", "מס' אישור הקצאה",
                    "Allocation Number", "Allocation No")
                {
                    // A proforma's advance approval is no allocation number; the approval's number
                    // a tax invoice prints ("מס' אישור הקצאה") is.
                    ExcludeLabels = new[] { "אישור הקצאה" },
                    WholeWord = true,
                },
                new FieldQuery(IdentifierKey,
                    "עוסק מורשה", "עוסק מורשה מס'", "עוסק מורשה מס.", "עוסק מורשה (ח.פ)", "ע.מ.", "ע.מ",
                    "עוסק פטור", "ח.פ.", "ח.פ", "ח\"פ", "ח.פ/ת.ז", "ח.פ./ת.ז.", "מס' עוסק", "מספר עוסק",
                    "מספר עוסק/ח.פ/ת.ז", "מס' חברה", "מספר חברה", "מספר מזהה", "ת.ז.", "ת.ז", "ת\"ז", "ע.ר.",
                    "ח.צ.", "ח.צ", "מס' תאגיד", "מספר תאגיד", "מס' זהות/תאגיד", "ח.פ לקוח", "ח.פ. לקוח",
                    "מספר תיק מדווח למע\"מ", "תיק מע\"מ",
                    "VAT No", "VAT Reg", "Tax ID", "Company No", "Company Reg"),
            };
        }

        // ----------------------------------------------------------------
        //  Entry points
        // ----------------------------------------------------------------

        /// <summary>Reads and validates the invoice in <paramref name="path"/>; its file name becomes the <see cref="IsraeliInvoice.Source"/>.</summary>
        public static IsraeliInvoice Extract(string path, IsraeliInvoiceOptions options = null)
        {
            _lastError = null;   // before the open, which may throw
            if (path == null) throw new ArgumentNullException(nameof(path));
            using var fs = File.OpenRead(path);
            var invoice = Extract(fs, options);
            invoice.Source ??= Path.GetFileName(path);
            return invoice;
        }

        /// <summary>Reads and validates the invoice in <paramref name="input"/>.</summary>
        /// <exception cref="PdfEncryptedDocumentException">
        /// The document is encrypted and no decryptor is registered.
        /// </exception>
        public static IsraeliInvoice Extract(Stream input, IsraeliInvoiceOptions options = null)
            => Extract(input, null, options);

        /// <summary>Reads and validates an encrypted invoice, opened with <paramref name="password"/>.</summary>
        /// <exception cref="PdfEncryptedDocumentException">
        /// The document is encrypted and no decryptor is registered.
        /// </exception>
        public static IsraeliInvoice Extract(Stream input, string password, IsraeliInvoiceOptions options)
        {
            _lastError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            options ??= new IsraeliInvoiceOptions();

            Stream seekable = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            Stream source = null;
            bool ownsSource = false;
            try
            {
                source = PdfPageRenderer.EnsureDecryptedStream(seekable, password, out ownsSource, out bool stillEncrypted);
                if (stillEncrypted)
                    throw new PdfEncryptedDocumentException(
                        "The invoice is encrypted and no decryptor is registered, so its text is ciphertext. " +
                        "Reference Tetitulz.Pdf.Encryption and call PdfEncryptionOperations.Register() once at startup.");
                return ExtractPlain(source, options);
            }
            finally
            {
                if (ownsSource) source.Dispose();
                if (ownsSeekable) seekable.Dispose();
            }
        }

        /// <summary>
        /// Reads and validates an invoice from page layouts the caller already has. No signature,
        /// embedded invoice or recognition is available from layouts alone; the item table is read
        /// with the text strategy.
        /// </summary>
        public static IsraeliInvoice Extract(IReadOnlyList<PdfPageTextLayout> pages, IsraeliInvoiceOptions options = null)
        {
            _lastError = null;
            if (pages == null) throw new ArgumentNullException(nameof(pages));
            options ??= new IsraeliInvoiceOptions();
            var clean = TextLayouts.WithoutNulls(pages);
            var invoice = new IsraeliInvoice { Source = options.Source };
            invoice.TextSource = clean.Any(HasText) ? InvoiceTextSource.TextLayer : InvoiceTextSource.None;
            ReadFromPages(invoice, clean, null, options);
            IsraeliInvoiceValidator.Validate(invoice, options.Validation);
            return invoice;
        }

        /// <summary>
        /// Reads and validates every document in the file at <paramref name="path"/> — a month's
        /// summary, its tax invoices and the report approved as another document, printed into one
        /// PDF — one <see cref="IsraeliInvoice"/> each, in page order; its file name becomes each
        /// one's <see cref="IsraeliInvoice.Source"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Where one document ends.</b> A document's amounts are stated on its own page: each
        /// page whose own subtotal and VAT (and exempt part) add up to one of its own totals ends a
        /// document, and the pages after the last such page — a list of trips, an appendix — are the
        /// last document's. Each document is read from its own pages alone, as <see cref="Extract(Stream, IsraeliInvoiceOptions)"/>
        /// reads a file.</para>
        /// <para><b>One document.</b> A file where one page adds up, or none, is one document: the
        /// list holds what <see cref="Extract(Stream, IsraeliInvoiceOptions)"/> returns. So does a
        /// file whose pages cannot be told apart this way — an invoice whose totals no page states
        /// in full.</para>
        /// <para>The signature, the text source and a recognised page's findings are the file's, and
        /// every document carries them. An embedded e-invoice describes one invoice: in a file of
        /// several documents it is applied to the one whose number it states, if any.</para>
        /// </remarks>
        public static IReadOnlyList<IsraeliInvoice> ExtractAll(string path, IsraeliInvoiceOptions options = null)
        {
            _lastError = null;   // before the open, which may throw
            if (path == null) throw new ArgumentNullException(nameof(path));
            using var fs = File.OpenRead(path);
            var invoices = ExtractAll(fs, options);
            foreach (var invoice in invoices) invoice.Source ??= Path.GetFileName(path);
            return invoices;
        }

        /// <summary>Reads and validates every document in <paramref name="input"/> (see <see cref="ExtractAll(string, IsraeliInvoiceOptions)"/>).</summary>
        /// <exception cref="PdfEncryptedDocumentException">
        /// The document is encrypted and no decryptor is registered.
        /// </exception>
        public static IReadOnlyList<IsraeliInvoice> ExtractAll(Stream input, IsraeliInvoiceOptions options = null)
            => ExtractAll(input, null, options);

        /// <summary>Reads and validates every document in an encrypted file, opened with <paramref name="password"/>.</summary>
        /// <exception cref="PdfEncryptedDocumentException">
        /// The document is encrypted and no decryptor is registered.
        /// </exception>
        public static IReadOnlyList<IsraeliInvoice> ExtractAll(Stream input, string password, IsraeliInvoiceOptions options)
        {
            _lastError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            options ??= new IsraeliInvoiceOptions();

            Stream seekable = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            Stream source = null;
            bool ownsSource = false;
            try
            {
                source = PdfPageRenderer.EnsureDecryptedStream(seekable, password, out ownsSource, out bool stillEncrypted);
                if (stillEncrypted)
                    throw new PdfEncryptedDocumentException(
                        "The invoice is encrypted and no decryptor is registered, so its text is ciphertext. " +
                        "Reference Tetitulz.Pdf.Encryption and call PdfEncryptionOperations.Register() once at startup.");
                var loaded = Load(source, options);
                var ranges = DocumentRanges(loaded.Pages, options);
                return ranges.Select(r => ReadDocument(loaded, r.Start, r.Count, options, several: ranges.Count > 1)).ToList();
            }
            finally
            {
                if (ownsSource) source.Dispose();
                if (ownsSeekable) seekable.Dispose();
            }
        }

        /// <summary>
        /// Reads and validates every document in page layouts the caller already has (see <see
        /// cref="ExtractAll(string, IsraeliInvoiceOptions)"/>). No signature, embedded invoice or
        /// recognition is available from layouts alone.
        /// </summary>
        public static IReadOnlyList<IsraeliInvoice> ExtractAll(IReadOnlyList<PdfPageTextLayout> pages, IsraeliInvoiceOptions options = null)
        {
            _lastError = null;
            if (pages == null) throw new ArgumentNullException(nameof(pages));
            options ??= new IsraeliInvoiceOptions();
            var clean = TextLayouts.WithoutNulls(pages).ToList();
            var loaded = new Loaded
            {
                Pages = clean,
                File = new IsraeliInvoice { TextSource = clean.Any(HasText) ? InvoiceTextSource.TextLayer : InvoiceTextSource.None },
            };
            var ranges = DocumentRanges(clean, options);
            return ranges.Select(r => ReadDocument(loaded, r.Start, r.Count, options, several: ranges.Count > 1)).ToList();
        }

        private static IsraeliInvoice ExtractPlain(Stream source, IsraeliInvoiceOptions options)
        {
            var loaded = Load(source, options);
            return ReadDocument(loaded, 0, loaded.Pages.Count, options, several: false);
        }

        /// <summary>What one opening of a file gives every document read from it.</summary>
        private sealed class Loaded
        {
            internal List<PdfPageTextLayout> Pages;
            internal IReadOnlyList<PdfPageGraphics> Graphics;
            /// <summary>The file's own: its text source, its signature, a recognised page's findings.</summary>
            internal IsraeliInvoice File;
            internal EmbeddedInvoice Embedded;
            internal string EmbeddedError;
        }

        private static Loaded Load(Stream source, IsraeliInvoiceOptions options)
        {
            var loaded = new Loaded { File = new IsraeliInvoice() };

            source.Position = 0;
            loaded.Pages = PdfStructuredTextExtractor.Extract(source, new WordAssemblyOptions { ApplyBidi = true }).ToList();

            if (options.ExtractLineItems)
            {
                source.Position = 0;
                loaded.Graphics = PdfGraphicsExtractor.Extract(source);
            }

            loaded.File.TextSource = loaded.Pages.Any(HasText) ? InvoiceTextSource.TextLayer : InvoiceTextSource.None;
            RecogniseUnreadablePages(source, loaded.Pages, options, loaded.File);

            source.Position = 0;
            using (var doc = PdfDocument.Open(source))
            {
                loaded.File.IsDigitallySigned = PdfSignatureDetection.HasSignature(doc.Trailer, doc.GetObject);
                ReportTextNotShown(doc, loaded.Pages, loaded.File);
            }

            if (options.ReadEmbeddedInvoice)
            {
                source.Position = 0;
                if (EmbeddedInvoiceReader.TryRead(source, out EmbeddedInvoice embedded)) loaded.Embedded = embedded;
                else loaded.EmbeddedError = EmbeddedInvoiceReader.LastError;
            }
            return loaded;
        }

        /// <summary>The shortest glyph a reader is taken to see, in points.</summary>
        private const float MinShownGlyphHeight = 1f;

        /// <summary>How many of the characters not shown a finding quotes.</summary>
        private const int MaxQuotedNotShown = 40;

        /// <summary>
        /// Says when a page holds text no reader sees, which is read with the rest: an invoice can show
        /// one total and carry another. Off the page, or under a point high, on any page; invisible or in
        /// a layer that is off only beside visible text, since a scan's recognised text is all invisible
        /// and is what there is to read. An invisible copy of visible text has gone by now
        /// (<c>DropInvisibleCopies</c>). Not told: text the colour of what it stands on, or behind an image,
        /// nor a <c>/ToUnicode</c> or <c>/ActualText</c> saying something other than the glyphs drawn.
        /// </summary>
        private static void ReportTextNotShown(PdfDocument doc, List<PdfPageTextLayout> pages, IsraeliInvoice file)
        {
            for (int i = 0; i < pages.Count && i < doc.Pages.Count; i++)
            {
                PdfArray box = PdfPageRenderer.ResolveRenderBox(doc.Pages[i], doc, null);
                float minX = (float)Math.Min(PdfResolve.ArrayDouble(doc, box, 0), PdfResolve.ArrayDouble(doc, box, 2));
                float maxX = (float)Math.Max(PdfResolve.ArrayDouble(doc, box, 0), PdfResolve.ArrayDouble(doc, box, 2));
                float minY = (float)Math.Min(PdfResolve.ArrayDouble(doc, box, 1), PdfResolve.ArrayDouble(doc, box, 3));
                float maxY = (float)Math.Max(PdfResolve.ArrayDouble(doc, box, 1), PdfResolve.ArrayDouble(doc, box, 3));

                bool anyVisible = false;
                foreach (PdfTextChar c in pages[i].Chars)
                    if (!c.IsInvisible && !string.IsNullOrWhiteSpace(c.Char)) { anyVisible = true; break; }

                int notShown = 0;
                var quoted = new System.Text.StringBuilder();
                foreach (PdfTextChar c in pages[i].Chars)
                {
                    if (string.IsNullOrWhiteSpace(c.Char)) continue;
                    bool offPage = c.X + c.Width < minX || c.X > maxX || c.Y + c.Height < minY || c.Y > maxY;
                    if (!offPage && Math.Abs(c.Height) >= MinShownGlyphHeight && !(c.IsInvisible && anyVisible)) continue;
                    if (notShown++ < MaxQuotedNotShown) quoted.Append(c.Char);
                }

                if (notShown > 0)
                    file.ExtractionFindings.Add(new InvoiceFinding(
                        InvoiceFindingSeverity.Warning, InvoiceFindingCodes.TextNotShown,
                        $"Page {i + 1} holds {notShown} character(s) a reader does not see — invisible, in a layer that is off, off the page or " +
                        $"under a point high — and they were read with the rest, so a value here may not be what the page " +
                        $"shows: '{quoted}'."));
            }
        }

        // One document of a loaded file — its pages [start, start + count) — read and validated.
        private static IsraeliInvoice ReadDocument(Loaded loaded, int start, int count, IsraeliInvoiceOptions options, bool several)
        {
            var invoice = new IsraeliInvoice
            {
                Source = options.Source,
                TextSource = loaded.File.TextSource,
                IsDigitallySigned = loaded.File.IsDigitallySigned,
            };
            foreach (var finding in loaded.File.ExtractionFindings) invoice.ExtractionFindings.Add(finding);

            var pages = start == 0 && count == loaded.Pages.Count ? loaded.Pages : loaded.Pages.GetRange(start, count);
            var graphics = loaded.Graphics == null || (start == 0 && count == loaded.Pages.Count)
                ? loaded.Graphics
                : loaded.Graphics.Skip(start).Take(count).ToList();
            ReadFromPages(invoice, pages, graphics, options);

            if (loaded.Embedded != null)
            {
                if (!several || string.Equals(loaded.Embedded.InvoiceNumber, invoice.InvoiceNumber, StringComparison.Ordinal))
                    ApplyEmbedded(invoice, loaded.Embedded);
            }
            else if (loaded.EmbeddedError != null && !several)
                invoice.ExtractionFindings.Add(new InvoiceFinding(
                    InvoiceFindingSeverity.Warning, InvoiceFindingCodes.EmbeddedInvoiceUnreadable,
                    $"The PDF carries an invoice-like attachment that could not be read, or " +
                    $"attachments that were not tried, so the page's values stand alone: " +
                    $"{loaded.EmbeddedError}."));

            IsraeliInvoiceValidator.Validate(invoice, options.Validation);
            return invoice;
        }

        /// <summary>
        /// The documents of a file as ranges of its pages (see <see cref="ExtractAll(string, IsraeliInvoiceOptions)"/>):
        /// each balanced page ends one, the pages after the last are its, and a file with fewer than
        /// two balanced pages is one document.
        /// </summary>
        private static List<(int Start, int Count)> DocumentRanges(List<PdfPageTextLayout> pages, IsraeliInvoiceOptions options)
        {
            var whole = new List<(int, int)> { (0, pages.Count) };
            if (pages.Count < 2) return whole;

            var moved = CurrencySignsInsideAmounts.MovedOut(pages);
            var queryList = CreateQueries(options.Validation?.VatRates);
            var queries = queryList.ToDictionary(q => q.Key);
            var prepared = PdfFieldExtractor.Prepare(moved, queryList);
            decimal tolerance = (options.Validation ?? new IsraeliInvoiceValidationOptions()).AmountTolerance;
            var totals = prepared.Ranked(queries[TotalKey], IsAmount);
            var ends = BalancedPages(prepared, queries, totals, tolerance)
                .Select(b => pages.FindIndex(p => p.PageNumber == b.Page))
                .Where(i => i >= 0)
                .Distinct()
                .OrderBy(i => i)
                .ToList();
            // One balanced page makes one range, the whole file, through the loop.
            if (ends.Count == 0) return whole;

            var ranges = new List<(int, int)>();
            int start = 0;
            for (int k = 0; k < ends.Count; k++)
            {
                int end = k == ends.Count - 1 ? pages.Count - 1 : ends[k];
                ranges.Add((start, end - start + 1));
                start = end + 1;
            }
            return ranges;
        }

        // ----------------------------------------------------------------
        //  Pages without readable text
        // ----------------------------------------------------------------

        private static bool HasText(PdfPageTextLayout page)
            => page?.Lines != null && page.Lines.Any(l => !string.IsNullOrWhiteSpace(l?.Text));

        /// <summary>
        /// Decodes each page whose text is legacy-encoded Hebrew (<see cref="DecodeLegacyHebrew"/>),
        /// replaces each page with no text by a layout built from recognised words — when
        /// <see cref="InvoiceOcrMode.WhenNeeded"/> and an engine answers — and records an
        /// extraction finding, and <see cref="LastError"/>, for each page that stays unread.
        /// </summary>
        private static void RecogniseUnreadablePages(Stream source, List<PdfPageTextLayout> pages,
                                                     IsraeliInvoiceOptions options, IsraeliInvoice invoice)
        {
            PdfOperations.OcrPages ocr = null;
            try
            {
                RecogniseUnreadablePages(source, pages, options, invoice, ref ocr);
            }
            finally
            {
                ocr?.Dispose();
            }
        }

        private static void RecogniseUnreadablePages(Stream source, List<PdfPageTextLayout> pages,
            IsraeliInvoiceOptions options, IsraeliInvoice invoice, ref PdfOperations.OcrPages ocr)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                bool empty = !HasText(pages[i]);
                if (!empty)
                {
                    if (!LooksLikeLegacyHebrew(pages[i])) continue;
                    // The letters are Windows-1255's at 0xE0–0xFA read as Latin-1: decoded in place,
                    // the page's own glyph boxes kept, each word read in visual order.
                    pages[i] = DecodeLegacyHebrew(pages[i]);
                    invoice.ExtractionFindings.Add(new InvoiceFinding(
                        InvoiceFindingSeverity.Warning, InvoiceFindingCodes.LegacyHebrewEncoding,
                        $"Page {i + 1}'s text was Hebrew in a legacy 8-bit encoding (Windows-1255) read as Latin " +
                        "letters — the fonts carry no Unicode mapping — and was decoded as such; check the Hebrew."));
                    continue;
                }

                if (options.Ocr == InvoiceOcrMode.WhenNeeded)
                {
                    // One reader for the document's pages: asked through the one-shot doors, each
                    // recognised page opened the whole document three times.
                    if (ocr == null)
                    {
                        if (options.OcrDpi <= 0)
                            throw new ArgumentOutOfRangeException(nameof(options), "OcrDpi must be positive.");
                        ocr = new PdfOperations.OcrPages(source, options.OcrDpi);
                    }
                    IReadOnlyList<PdfOcrWord> words = ocr.Recognize(i, clearLastError: true, out _);
                    if (words != null && words.Count > 0)
                    {
                        // The words are in unrotated user space; the layout is built as displayed.
                        int rotation = ocr.Size(i)?.Rotation ?? 0;
                        pages[i] = OcrTextLayout.Build(pages[i].PageNumber, words, rotation);
                        invoice.TextSource = InvoiceTextSource.Ocr;
                        continue;
                    }
                    _lastError = PdfOperations.LastOcrError ?? $"page {i + 1}: recognition found no text";
                }
                else
                {
                    _lastError = $"page {i + 1}: no text layer, and recognition is off";
                }

                invoice.ExtractionFindings.Add(new InvoiceFinding(
                    InvoiceFindingSeverity.Warning, InvoiceFindingCodes.NoTextLayer,
                    $"Page {i + 1} has no text layer" +
                    (options.Ocr == InvoiceOcrMode.WhenNeeded
                        ? $", and recognition produced nothing: {_lastError}"
                        : ", and recognition is off.")));
            }
        }

        /// <summary>
        /// Whether a page's text reads like Hebrew bytes (Windows-1255 / ISO 8859-8, letters at
        /// 0xE0–0xFA) decoded as Latin-1: at least six of those Latin letters — à to ú, without ÷ —
        /// either in runs of three or more, or making up most of the page's letters; and fewer
        /// real Hebrew letters than that.
        /// </summary>
        /// <remarks>
        /// A French or Spanish word mixes its accented letters with plain ones, so it makes no run
        /// and no majority. The majority test is there because a font's widths that do not fit
        /// the glyphs split a word into one- and two-letter pieces, and no run survives that.
        /// </remarks>
        internal static bool LooksLikeLegacyHebrew(PdfPageTextLayout page)
        {
            int inRuns = 0, latin1 = 0, letters = 0, hebrew = 0;
            foreach (var line in page.Lines ?? Array.Empty<PdfTextLine>())
            {
                string t = line?.Text ?? string.Empty;
                int run = 0;
                for (int i = 0; i <= t.Length; i++)
                {
                    char c = i < t.Length ? t[i] : ' ';
                    if (char.IsLetter(c)) letters++;
                    if (c >= '\u05D0' && c <= '\u05EA') hebrew++;
                    if (c >= '\u00E0' && c <= '\u00FA' && c != '\u00F7') { run++; latin1++; continue; }
                    if (run >= 3) inRuns += run;
                    run = 0;
                }
            }
            bool shaped = inRuns >= 6 || (latin1 >= 6 && latin1 * 2 > letters);
            return shaped && hebrew < latin1;
        }

        /// <summary>
        /// <paramref name="page"/> with its legacy Hebrew decoded: each character Windows-1255 puts a
        /// Hebrew letter or point at (<see cref="Windows1255Hebrew"/>) becomes that letter, right to
        /// left, its glyph box kept; a word holding one is read in visual order — a legacy font draws
        /// Hebrew as it is shown, the last letter leftmost — and a line whose words mostly are is
        /// right to left. Every other character, a number among them, stays as it is.
        /// </summary>
        internal static PdfPageTextLayout DecodeLegacyHebrew(PdfPageTextLayout page)
        {
            var lines = new List<PdfTextLine>();
            foreach (var line in TextLayouts.LinesOf(page))
            {
                var words = new List<PdfTextWord>();
                foreach (var word in TextLayouts.WordsOf(line))
                {
                    var glyphs = TextDirection.VisualGlyphs(word);
                    var mapped = new List<PdfTextChar>(glyphs.Count);
                    bool hebrew = false;
                    foreach (var g in glyphs)
                    {
                        if (g.Char.Length == 1 && Windows1255Hebrew(g.Char[0], out char decoded))
                        {
                            hebrew = true;
                            mapped.Add(new PdfTextChar(decoded.ToString(), g.X, g.Y, g.Width, g.Height, isRtl: true)
                            {
                                IsInvisible = g.IsInvisible,
                                InReversedRun = g.InReversedRun,
                                Style = g.Style,
                            });
                        }
                        else mapped.Add(g);
                    }
                    if (!hebrew) { words.Add(word); continue; }

                    // Logical order is the visual order read from the right.
                    var logical = mapped.OrderByDescending(g => g.X).ToList();
                    words.Add(new PdfTextWord(string.Concat(logical.Select(g => g.Char)),
                                              word.X, word.Y, word.Width, word.Height, logical, isRtl: true));
                }
                bool rtl = words.Count(w => w.IsRtl) * 2 > words.Count;
                lines.Add(new PdfTextLine(TextDirection.ToReadingOrder(words, rtl), line.X, line.Y, line.Width, line.Height,
                                          words, rtl) { IsVertical = line.IsVertical });
            }
            return new PdfPageTextLayout(page.PageNumber, lines);
        }

        /// <summary>
        /// Windows-1255's Hebrew, read as the Latin-1 character of the same byte: the letters alef to
        /// tav with the final forms among them at 0xE0–0xFA (ISO-8859-8's order too), the points
        /// sheva to rafe at 0xC0–0xCF, the punctuation and marks at 0xD0–0xD3, and the Yiddish
        /// ligatures and the geresh and gershayim at 0xD4–0xD8 — the code page's own table, which
        /// the test holds this to.
        /// </summary>
        internal static bool Windows1255Hebrew(char latin1, out char hebrew)
        {
            if (latin1 >= 'à' && latin1 <= 'ú') { hebrew = (char)(0x05D0 + (latin1 - 0xE0)); return true; }
            if (latin1 >= 'À' && latin1 <= 'Ó') { hebrew = (char)(0x05B0 + (latin1 - 0xC0)); return true; }
            if (latin1 >= 'Ô' && latin1 <= 'Ø') { hebrew = (char)(0x05F0 + (latin1 - 0xD4)); return true; }
            hebrew = '\0';
            return false;
        }

        // ----------------------------------------------------------------
        //  Reading the values
        // ----------------------------------------------------------------

        private static void ReadFromPages(IsraeliInvoice invoice, IReadOnlyList<PdfPageTextLayout> pages,
                                          IReadOnlyList<PdfPageGraphics> graphics, IsraeliInvoiceOptions options)
        {
            // Word draws "₪ 3,539.43" as "3 ₪,539.43": the fields and the item table read the
            // amount whole.
            pages = CurrencySignsInsideAmounts.MovedOut(pages);
            var queryList = CreateQueries(options.Validation?.VatRates);
            var queries = queryList.ToDictionary(q => q.Key);
            var prepared = PdfFieldExtractor.Prepare(pages, queryList);

            FieldResult Record(FieldResult r)
            {
                invoice.Fields.Add(r);
                return r;
            }
            FieldResult Best(string key, Func<FieldResult, bool> accept, Func<PdfFieldExtractor.Candidate, bool> where = null)
                => Record(prepared.Best(queries[key], accept, where));

            invoice.DocumentType = DetectDocumentType(prepared);
            invoice.StatesInputVatNotDeductible = prepared.Lines.Any(kl => kl.Key.Contains(InputVatNotDeductibleKey));

            decimal tolerance = (options.Validation ?? new IsraeliInvoiceValidationOptions()).AmountTolerance;
            var totals = prepared.Ranked(queries[TotalKey], IsAmount);
            var balanced = BalancedPage(prepared, queries, totals, tolerance) ?? HeadedTotals(prepared, queries, tolerance);
            // A number printed on the page the amounts are read from, before one anywhere else.
            Func<PdfFieldExtractor.Candidate, bool> onBalancedPage =
                balanced == null ? null : c => c.Line.Page.PageNumber == balanced.Page;

            // The number a label names, else one printed after the title, on the title's line, and
            // not a date — after a title, not after a sentence's words that name one; of the lines
            // `where` takes. Null when neither is found, and nothing recorded.
            string ReadNumber(Func<PdfFieldExtractor.Candidate, bool> where)
            {
                var labelled = prepared.Best(queries[InvoiceNumberKey], r => FirstToken(r.Value) != null, where);
                if (labelled.Label != null) return FirstToken(Record(labelled).Value);
                var titled = prepared.Best(queries[TitleNumberKey],
                                           r => r.Confidence >= 0.9f && DocumentNumberIn(r.Value) != null,
                                           c => StandsAsTitle(c) && (where == null || where(c)));
                return titled.Label != null ? DocumentNumberIn(Record(titled).Value) : null;
            }

            invoice.InvoiceNumber = (balanced != null ? ReadNumber(onBalancedPage) : null) ?? ReadNumber(null);
            if (invoice.InvoiceNumber == null)
            {
                Record(new FieldResult { Key = InvoiceNumberKey });
                Record(new FieldResult { Key = TitleNumberKey });
            }

            var date = Best(DateKey, r => InvoiceValueParser.TryFindDate(r.Value, out _));
            if (InvoiceValueParser.TryFindDate(date.Value, out DateTime d)) invoice.Date = d;
            else invoice.Date = FirstDateOnFirstPage(prepared);

            string currency = null;
            var subtotal = Record(balanced?.Subtotal ?? prepared.Best(queries[SubtotalKey], IsAmount));
            if (InvoiceValueParser.TryParseAmount(subtotal.Value, out decimal sub, out string c1))
            { invoice.Subtotal = sub; currency ??= c1; }

            var exempt = Record(balanced != null ? balanced.Exempt : prepared.Best(queries[ExemptKey], IsAmount));
            if (InvoiceValueParser.TryParseAmount(exempt.Value, out decimal ex, out _)) invoice.ExemptAmount = ex;

            var vat = Record(balanced?.Vat
                             ?? prepared.Best(queries[VatKey], r => IsAmount(r) || InvoiceValueParser.TryParsePercent(r.Value, out _)));
            if (InvoiceValueParser.TryParseAmount(vat.Value, out decimal v, out string c2))
            { invoice.Vat = v; currency ??= c2; }
            if (balanced?.RatedParts != null)
                foreach (var part in balanced.RatedParts) invoice.RatedSubtotals.Add(part);
            // Taxed at several rates, the invoice has no one rate.
            bool oneRate = invoice.RatedSubtotals.Count < 2;
            if (oneRate && InvoiceValueParser.TryParsePercent(vat.Value, out decimal rate)) invoice.VatRate = rate;
            // Else the rate a subtotal's label named: "סה"כ סכומים ב-17% מע"מ".
            else if (oneRate && InvoiceValueParser.TryParsePercent(subtotal.Label, out rate)) invoice.VatRate = rate;
            // Else the rate a table printed in its own column (HeadedTotals).
            else if (oneRate && balanced?.Rate != null) invoice.VatRate = balanced.Rate;

            var total = Record(balanced?.Total ?? ChooseTotal(totals, invoice, tolerance, queries[TotalKey]));
            // A receipt printing no total is for what it says was paid.
            if (string.IsNullOrEmpty(total.Value) && invoice.DocumentType == IsraeliDocumentType.Receipt)
                total = Best(PaidKey, IsAmount);
            if (InvoiceValueParser.TryParseAmount(total.Value, out decimal tot, out string c3))
            { invoice.Total = tot; currency = c3 ?? currency; }
            invoice.Currency = currency;

            // No subtotal's label, but a total's line the VAT completes to the total: the
            // "סה"כ / מע"מ / סה"כ כולל מע"מ" an invoice stacks is its subtotal, VAT and total.
            if (invoice.Subtotal == null && invoice.Vat != null && invoice.Total != null)
            {
                var completed = totals.FirstOrDefault(r => !ReferenceEquals(r, total) && r.PageIndex == total.PageIndex
                    && InvoiceValueParser.TryParseAmount(r.Value, out decimal part, out _)
                    && Reconciles(total, part, invoice.Vat.Value, invoice.ExemptAmount, tolerance));
                if (completed != null && InvoiceValueParser.TryParseAmount(completed.Value, out decimal before, out _))
                {
                    invoice.Subtotal = before;
                    invoice.Fields.Remove(subtotal);
                    Record(new FieldResult
                    {
                        Key = SubtotalKey,
                        Label = completed.Label,
                        Value = completed.Value,
                        PageIndex = completed.PageIndex,
                        ValueBox = completed.ValueBox,
                        Confidence = completed.Confidence,
                    });
                }
            }

            if (invoice.Subtotal == null && invoice.ExemptAmount == null && invoice.Vat != null && invoice.Total != null
                && invoice.VatRate != null)
                ReadTaxedAndExemptColumns(invoice, prepared, queries, total, subtotal, exempt, tolerance, Record);

            if (invoice.Subtotal != null && invoice.ExemptAmount == null && invoice.Vat != null && invoice.Total != null)
                ReadStatedExemptPart(invoice, prepared, total, tolerance, Record);

            if (invoice.DocumentType != IsraeliDocumentType.Proforma)
            {
                var allocation = Best(AllocationNumberKey, r => AllocationNumberIn(r.Value) != null);
                invoice.AllocationNumber = AllocationNumberIn(allocation.Value);
            }

            ReadIdentifiers(invoice, prepared, queries[IdentifierKey]);

            if (options.ExtractLineItems)
                invoice.LineItems = InvoiceLineItems.Read(pages, graphics);

            if (prepared.Incomplete)
            {
                _lastError = prepared.IncompleteReason;
                invoice.ExtractionFindings.Add(new InvoiceFinding(
                    InvoiceFindingSeverity.Warning, InvoiceFindingCodes.ExtractionIncomplete,
                    "The field search stopped at its work budget (PdfLimits.MaxFieldExtractionWork): " +
                    "some label matches were never examined, and a value may be missing or not the best."));
            }
        }

        // The amounts a line prints, word by word, in reading order: each word that is an amount alone.
        private static List<(PdfTextWord Word, decimal Amount)> LineAmounts(PdfFieldExtractor.KeyedLine kl)
        {
            var amounts = new List<(PdfTextWord, decimal)>();
            foreach (var w in kl.Line.Words)
                if (InvoiceValueParser.TryParseAmountValue(PdfFieldExtractor.WordReadingOrder(w), out decimal a, out _))
                    amounts.Add((w, a));
            return amounts;
        }

        // Whether VAT at a rate on an amount is the VAT printed: to a cent per hundred shekels of VAT,
        // and never less than the tolerance — a VAT summed line by line rounds apart from one computed once.
        private static bool VatAtRate(decimal amount, decimal rate, decimal vat, decimal tolerance)
        {
            try
            {
                return Math.Abs(amount * rate / 100m - vat) <= Math.Max(tolerance, Math.Abs(vat) / 100m);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        /// <summary>
        /// A totals row of a taxed and an exempt column — a parking operator's <c>סה"כ 1,000.00 ₪
        /// 300.00 ₪</c> under <c>חייב מע"מ | לא חייב מע"מ</c>, its VAT and total on lines of their own:
        /// two amounts of a total's line on the total's page that, with the VAT, make the total, the
        /// one the VAT is the rate of being the subtotal and the other the exempt part. Read only
        /// when no subtotal and no exempt part were found and the rate is known, which tells the two
        /// columns apart; a pair the rate fits both ways, or no pair, reads nothing.
        /// </summary>
        private static void ReadTaxedAndExemptColumns(IsraeliInvoice invoice, PdfFieldExtractor.Prepared prepared,
                                                      Dictionary<string, FieldQuery> queries, FieldResult total,
                                                      FieldResult subtotal, FieldResult exempt, decimal tolerance,
                                                      Func<FieldResult, FieldResult> record)
        {
            decimal vat = invoice.Vat.Value, rate = invoice.VatRate.Value;
            foreach (var c in prepared.All(queries[TotalKey]))
            {
                if (c.Line.Page.PageNumber != total.PageIndex) continue;
                var amounts = LineAmounts(c.Line);
                var fits = new List<((PdfTextWord Word, decimal Amount) Taxed, (PdfTextWord Word, decimal Amount) Free)>();
                for (int i = 0; i < amounts.Count; i++)
                    for (int j = 0; j < amounts.Count; j++)
                    {
                        if (i == j) continue;
                        if (!Reconciles(total, amounts[i].Amount, vat, amounts[j].Amount, tolerance)) continue;
                        if (VatAtRate(amounts[i].Amount, rate, vat, tolerance)) fits.Add((amounts[i], amounts[j]));
                    }
                if (fits.Count != 1) continue;

                var (taxed, free) = fits[0];
                invoice.Subtotal = taxed.Amount;
                invoice.ExemptAmount = free.Amount;
                invoice.Fields.Remove(subtotal);
                invoice.Fields.Remove(exempt);
                record(ColumnResult(SubtotalKey, c, taxed.Word));
                record(ColumnResult(ExemptKey, c, free.Word));
                return;
            }
        }

        private static FieldResult ColumnResult(string key, PdfFieldExtractor.Candidate c, PdfTextWord word)
            => new FieldResult
            {
                Key = key,
                Label = c.Result.Label,
                Value = PdfFieldExtractor.WordReadingOrder(word),
                PageIndex = c.Line.Page.PageNumber,
                ValueBox = (word.X, word.Y, word.Width, word.Height),
                Confidence = c.Result.Confidence,
            };

        /// <summary>
        /// The exempt part a page states as a difference: a leasing invoice's totals row sums its
        /// taxed columns and its untaxed ones (a licence fee, compulsory insurance) under no label of
        /// their own, so its subtotal and VAT fall short of its total by the untaxed part — and the
        /// page prints the total less the VAT, the sum before VAT, as an amount. When it does, the
        /// shortfall is the exempt part. A shortfall with no such amount on the total's page is left
        /// for the validator to report.
        /// </summary>
        private static void ReadStatedExemptPart(IsraeliInvoice invoice, PdfFieldExtractor.Prepared prepared,
                                                 FieldResult total, decimal tolerance, Func<FieldResult, FieldResult> record)
        {
            decimal beforeVat, gap;
            try
            {
                beforeVat = invoice.Total.Value - invoice.Vat.Value;
                gap = beforeVat - invoice.Subtotal.Value;
            }
            catch (OverflowException)
            {
                return;
            }
            if (gap <= tolerance) return;

            foreach (var kl in prepared.Lines)
            {
                if (kl.Page.PageNumber != total.PageIndex) continue;
                foreach (var (word, amount) in LineAmounts(kl))
                {
                    if (Math.Abs(amount - beforeVat) > tolerance) continue;
                    invoice.ExemptAmount = gap;
                    var stated = invoice.Fields.FirstOrDefault(f => f.Key == ExemptKey);
                    if (stated != null) invoice.Fields.Remove(stated);
                    record(new FieldResult
                    {
                        Key = ExemptKey,
                        Value = gap.ToString("0.00", CultureInfo.InvariantCulture),
                        PageIndex = kl.Page.PageNumber,
                        ValueBox = (word.X, word.Y, word.Width, word.Height),
                        Confidence = PdfFieldExtractor.NoAmountConfidence,
                    });
                    return;
                }
            }
        }

        // An amount the value is, not one a sentence holds: "30 ימים" after a total's label in prose is
        // no total.
        private static bool IsAmount(FieldResult r) => InvoiceValueParser.TryParseAmountValue(r.Value, out _, out _);

        /// <summary>
        /// The total: of the total query's matches that are amounts (<paramref name="ranked"/>, best
        /// first), the best-ranked one the subtotal and the VAT (and the exempt part) add up to,
        /// when they are known; else the best-ranked. A paid invoice prints "Balance Due 0.00"
        /// after its total.
        /// </summary>
        private static FieldResult ChooseTotal(List<FieldResult> ranked, IsraeliInvoice invoice, decimal tolerance,
                                               FieldQuery query)
        {
            if (ranked.Count == 0) return new FieldResult { Key = query.Key };
            if (invoice.Subtotal != null && invoice.Vat != null)
                foreach (var r in ranked)
                    if (Reconciles(r, invoice.Subtotal.Value, invoice.Vat.Value, invoice.ExemptAmount, tolerance)) return r;
            return ranked[0];
        }

        private static bool Reconciles(FieldResult r, decimal subtotal, decimal vat, decimal? exempt, decimal tolerance)
        {
            if (!InvoiceValueParser.TryParseAmount(r.Value, out decimal total, out _)) return false;
            try
            {
                decimal sum = subtotal + vat;
                return Math.Abs(sum - total) <= tolerance
                       || (exempt != null && Math.Abs(sum + exempt.Value - total) <= tolerance);
            }
            catch (OverflowException)
            {
                // Amounts too large to add reconcile with nothing; the validator reports them.
                return false;
            }
        }

        /// <summary>The amounts one page states, and which page.</summary>
        private sealed class PageTotals
        {
            internal int Page;
            internal FieldResult Subtotal, Exempt, Vat, Total;
            /// <summary>The VAT rate a table prints in a column of its own, when it does.</summary>
            internal decimal? Rate;
            /// <summary>The subtotal's parts at several rates, when the page states them; else null.</summary>
            internal List<IsraeliRatedAmount> RatedParts;
        }

        // The words a totals row of a table opens with: "סה"כ כללי לתשלום:", "סך הכל", "Total".
        private static readonly string[] TotalsRowKeys = { "סה\"כ", "סך הכל", "total" };

        /// <summary>How many lines under its headings a table's totals row may be printed.</summary>
        private const int MaxTableRows = 40;

        /// <summary>
        /// The amounts a table prints under its column headings and states in its totals row — a
        /// fuel retailer's monthly report heads its columns <c>סה"כ לפני מע"מ | אחוז מע"מ | סה"כ מע"מ |
        /// סה"כ כולל מע"מ</c> and closes them with <c>סה"כ כללי לתשלום:</c> — or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>A heading is no label for the value under it (<see cref="PdfFieldExtractor"/> passes
        /// over a value below a heading whose column holds more), so the subtotal, the VAT and the
        /// total are read here: on a line holding a heading of each, in distinct columns, the first
        /// line below it on its page, within <see cref="MaxTableRows"/>, that opens with a total's
        /// word and holds an amount under each heading. They are taken only when the subtotal and
        /// the VAT add up to the total.</para>
        /// <para>The rate is the column under the header's rate heading (<c>אחוז מע"מ</c>, which the VAT
        /// query excludes: it names the rate, not the VAT) when every row between the headings and the
        /// totals row prints the same number there and the subtotal at that rate is the VAT.</para>
        /// </remarks>
        private static PageTotals HeadedTotals(PdfFieldExtractor.Prepared prepared, Dictionary<string, FieldQuery> queries,
                                               decimal tolerance)
        {
            var subtotals = prepared.All(queries[SubtotalKey]);
            if (subtotals.Count == 0) return null;
            var vats = prepared.All(queries[VatKey]);
            var totals = prepared.All(queries[TotalKey]);
            var totalsRowKeys = TotalsRowKeys.Select(InvoiceValueParser.LabelKey).ToList();

            foreach (var s in subtotals)
            {
                var header = s.Line;
                var headerVats = vats.Where(c => ReferenceEquals(c.Line, header) && !SameColumn(c, s)).ToList();
                var headerTotals = totals.Where(c => ReferenceEquals(c.Line, header) && !SameColumn(c, s)).ToList();
                if (headerVats.Count == 0 || headerTotals.Count == 0) continue;

                var rows = prepared.Lines.Where(kl => ReferenceEquals(kl.Page, header.Page)
                                                      && kl.Order > header.Order && kl.Order <= header.Order + MaxTableRows).ToList();
                int totalsRow = rows.FindIndex(kl => totalsRowKeys.Any(k => kl.Key.StartsWith(k, StringComparison.Ordinal)));
                if (totalsRow < 0) continue;
                var row = rows[totalsRow];
                var sub = AmountUnder(row, s);
                if (sub == null) continue;

                foreach (var v in headerVats)
                foreach (var t in headerTotals)
                {
                    if (SameColumn(v, t)) continue;
                    var vat = AmountUnder(row, v);
                    var total = AmountUnder(row, t);
                    if (vat == null || total == null
                        || !InvoiceValueParser.TryParseAmount(sub.Value, out decimal subAmount, out _)
                        || !InvoiceValueParser.TryParseAmount(vat.Value, out decimal vatAmount, out _)
                        || !Reconciles(total, subAmount, vatAmount, null, tolerance)) continue;
                    return new PageTotals
                    {
                        Page = header.Page.PageNumber,
                        Subtotal = Headed(sub, SubtotalKey, s),
                        Exempt = new FieldResult { Key = ExemptKey },
                        Vat = Headed(vat, VatKey, v),
                        Total = Headed(total, TotalKey, t),
                        Rate = RateColumn(header, rows.Take(totalsRow), subAmount, vatAmount, tolerance),
                    };
                }
            }
            return null;
        }

        // Whether two headings share a column: their label words overlap horizontally.
        private static bool SameColumn(PdfFieldExtractor.Candidate a, PdfFieldExtractor.Candidate b)
            => a.Own.Left < b.Own.Right && b.Own.Left < a.Own.Right;

        // The amount a row prints under a heading: its words over the heading's label, read as a value.
        private static FieldResult AmountUnder(PdfFieldExtractor.KeyedLine row, PdfFieldExtractor.Candidate heading)
            => AmountUnder(row, heading.Own.Left, heading.Own.Right);

        private static FieldResult AmountUnder(PdfFieldExtractor.KeyedLine row, float headingLeft, float headingRight)
        {
            var cell = row.Line.Words.Where(w => w.X < headingRight && w.X + w.Width > headingLeft).ToList();
            if (cell.Count == 0) return null;
            string value = string.Join(" ", cell.Select(PdfFieldExtractor.WordReadingOrder));
            if (!InvoiceValueParser.TryParseAmountValue(value, out _, out _)) return null;
            float left = cell.Min(w => w.X), bottom = cell.Min(w => w.Y);
            return new FieldResult
            {
                Value = value,
                PageIndex = row.Page.PageNumber,
                ValueBox = (left, bottom, cell.Max(w => w.X + w.Width) - left, cell.Max(w => w.Y + w.Height) - bottom),
                Confidence = 0.6f,
            };
        }

        private static FieldResult Headed(FieldResult cell, string key, PdfFieldExtractor.Candidate heading)
        {
            cell.Key = key;
            cell.Label = heading.Result.Label;
            return cell;
        }

        // A rate column's heading word: "אחוז", "שיעור", "שעור" (of "אחוז מע"מ").
        private static readonly HashSet<string> RateHeadingKeys = new HashSet<string>(
            new[] { "אחוז", "שיעור", "שעור" }.Select(InvoiceValueParser.LabelKey), StringComparer.Ordinal);

        // The one number every row prints under the header's rate heading (a word of RateHeadingKeys
        // followed by a VAT word), when the subtotal at that rate is the VAT; else null.
        private static decimal? RateColumn(PdfFieldExtractor.KeyedLine header, IEnumerable<PdfFieldExtractor.KeyedLine> rows,
                                           decimal subtotal, decimal vat, decimal tolerance)
        {
            var body = rows.ToList();
            if (body.Count == 0) return null;
            var words = header.Line.Words;
            string vatKey = InvoiceValueParser.LabelKey("מע\"מ");
            foreach (var w in words)
            {
                if (!RateHeadingKeys.Contains(InvoiceValueParser.LabelKey(PdfFieldExtractor.WordReadingOrder(w)))) continue;
                // The VAT word that completes the heading: the nearest word of the line that is one.
                var vatWord = words.Where(o => InvoiceValueParser.LabelKey(PdfFieldExtractor.WordReadingOrder(o)) == vatKey)
                                   .OrderBy(o => Math.Abs((o.X + o.Width / 2) - (w.X + w.Width / 2))).FirstOrDefault();
                if (vatWord == null) continue;
                float left = Math.Min(w.X, vatWord.X), right = Math.Max(w.X + w.Width, vatWord.X + vatWord.Width);
                decimal? rate = null;
                bool one = true;
                foreach (var kl in body)
                {
                    var cell = AmountUnder(kl, left, right);
                    if (cell == null || !InvoiceValueParser.TryParseAmount(cell.Value, out decimal n, out _)) continue;
                    if (rate != null && rate != n) { one = false; break; }
                    rate = n;
                }
                if (!one || rate == null || rate <= 0 || rate >= 100) continue;
                try
                {
                    if (Math.Abs(subtotal * rate.Value / 100m - vat) <= tolerance) return rate;
                }
                catch (OverflowException) { }
            }
            return null;
        }

        /// <summary>
        /// The first page, in page order, whose own subtotal and VAT (and its exempt part, when it
        /// prints one) add up to one of its own totals — each the page's best-ranked match — or
        /// <c>null</c> when no page does.
        /// </summary>
        /// <remarks>
        /// A file can hold several documents: a fuel retailer's month is a summary page, a tax
        /// invoice a page, a second, and the fuel report approved as another document; a toll
        /// operator's invoice is one page and its trips the rest. Each document states its amounts
        /// on its own page, and the best match for each amount across the file mixes them — a VAT
        /// from a later page beside the invoice's own subtotal. The first page that balances is
        /// the first document's, and its amounts are read together. One invoice, on one page or
        /// many, balances on the page that holds its totals, as before.
        /// </remarks>
        private static PageTotals BalancedPage(PdfFieldExtractor.Prepared prepared, Dictionary<string, FieldQuery> queries,
                                               List<FieldResult> totals, decimal tolerance)
            => BalancedPages(prepared, queries, totals, tolerance).FirstOrDefault();

        // A subtotal's value that names a rate of 0%: "% 0 50.00".
        private static bool IsZeroRated(FieldResult r)
            => InvoiceValueParser.TryParsePercent(r.Value, out decimal rate) && rate == 0m;

        // Every page that balances, in page order (BalancedPage's rule).
        private static IEnumerable<PageTotals> BalancedPages(PdfFieldExtractor.Prepared prepared, Dictionary<string, FieldQuery> queries,
                                                             List<FieldResult> totals, decimal tolerance)
        {
            var subtotals = prepared.Ranked(queries[SubtotalKey], IsAmount);
            if (subtotals.Count == 0 || totals.Count == 0) yield break;
            var vats = prepared.Ranked(queries[VatKey], IsAmount);
            var exempts = prepared.Ranked(queries[ExemptKey], IsAmount);

            foreach (int page in subtotals.Select(r => r.PageIndex).Distinct().OrderBy(p => p))
            {
                // A subtotal taxed at 0% — "סה"כ חייב במע"מ % 0 50.00" beside the one at 18% — is
                // zero-rated: the exempt part, when the page prints no exempt line of its own.
                var pageSubtotals = subtotals.Where(r => r.PageIndex == page).ToList();
                var zeroRated = pageSubtotals.Where(IsZeroRated).ToList();
                // Subtotals at several rates — "ב-17% מע"מ" and "ב-18% מע"מ" on a bill across a rate
                // change — are one subtotal, their sum, in parts.
                var rated = RatedParts(pageSubtotals.Where(r => !IsZeroRated(r)));
                var s = rated.Count >= 2 ? SumOfParts(rated, page) : pageSubtotals.FirstOrDefault(r => !IsZeroRated(r));
                var v = vats.FirstOrDefault(r => r.PageIndex == page);
                if (s == null || v == null
                    || !InvoiceValueParser.TryParseAmount(s.Value, out decimal sub, out _)
                    || !InvoiceValueParser.TryParseAmount(v.Value, out decimal vat, out _)) continue;
                var e = exempts.FirstOrDefault(r => r.PageIndex == page);
                // One zero-rated line: its labels' several matches ("סה"כ חייב במע"מ", "חייב במע"מ") are one value.
                if (e == null && zeroRated.Count > 0 && zeroRated.Select(r => (r.Value, r.ValueBox)).Distinct(SubtotalKeys).Count() == 1)
                    e = new FieldResult
                    {
                        Key = ExemptKey,
                        Label = zeroRated[0].Label,
                        Value = zeroRated[0].Value,
                        PageIndex = zeroRated[0].PageIndex,
                        ValueBox = zeroRated[0].ValueBox,
                        Confidence = zeroRated[0].Confidence,
                    };
                decimal? ex = e != null && InvoiceValueParser.TryParseAmount(e.Value, out decimal x, out _) ? x : (decimal?)null;

                var t = totals.FirstOrDefault(r => r.PageIndex == page && Reconciles(r, sub, vat, ex, tolerance));
                if (t != null)
                    yield return new PageTotals
                    {
                        Page = page,
                        Subtotal = s,
                        Exempt = e ?? new FieldResult { Key = ExemptKey },
                        Vat = v,
                        Total = t,
                        RatedParts = rated.Count >= 2 ? rated.Select(p => p.Part).ToList() : null,
                    };
            }
        }

        // A subtotal's value and box, the value being the document's text, hash under a key the document
        // cannot see (KeyedHash): the tuple's own hash is the runtime's string hash, which .NET Framework
        // does not key.
        private static readonly IEqualityComparer<(string, (float, float, float, float))> SubtotalKeys =
            Core.Util.KeyedHash.Comparer<(string, (float, float, float, float))>(
                k => unchecked(Core.Util.KeyedHash.Of(k.Item1) * 31 + k.Item2.GetHashCode()));

        // The subtotal lines that name their rate in their label ("סה"כ סכומים ב-17% מע"מ"), one per
        // line (a line matched by two labels is one), when they name two rates or more; else none.
        private static List<(IsraeliRatedAmount Part, FieldResult Result)> RatedParts(IEnumerable<FieldResult> subtotals)
        {
            var parts = new List<(IsraeliRatedAmount, FieldResult)>();
            var seen = new HashSet<(string, (float, float, float, float))>(SubtotalKeys);
            foreach (var r in subtotals)
            {
                if (!seen.Add((r.Value, r.ValueBox))) continue;
                if (!InvoiceValueParser.TryParsePercent(r.Label, out decimal rate) || rate <= 0m
                    || !InvoiceValueParser.TryParseAmount(r.Value, out decimal amount, out _)) continue;
                parts.Add((new IsraeliRatedAmount(rate, amount), r));
            }
            return parts.Select(p => p.Item1.Rate).Distinct().Count() >= 2 ? parts : new List<(IsraeliRatedAmount, FieldResult)>();
        }

        // The subtotal a page's rated parts make: their sum, at the first part's label and place.
        private static FieldResult SumOfParts(List<(IsraeliRatedAmount Part, FieldResult Result)> parts, int page)
        {
            decimal sum;
            try { sum = parts.Sum(p => p.Part.Amount); }
            catch (OverflowException) { return null; }
            var first = parts[0].Result;
            return new FieldResult
            {
                Key = SubtotalKey,
                Label = first.Label,
                Value = sum.ToString("0.00", CultureInfo.InvariantCulture),
                PageIndex = page,
                ValueBox = first.ValueBox,
                Confidence = first.Confidence,
            };
        }

        /// <summary>
        /// The document's type from its title: the first line holding a title's words in order —
        /// whole words, the joining punctuation of <c>חשבונית מס / קבלה</c> and <c>חשבונית מס –
        /// זיכוי</c> dropped, so <c>חשבונית מספר</c> and <c>חשבונית מסחרית</c> are no tax invoice —
        /// where they stand as a title (<see cref="StandsAsTitle(List{TitleWord}, int, int)"/>).
        /// Failing that, a receipt word standing as one. A line that names a title in a sentence
        /// (<c>רשאי להוציא חשבונית מס</c>), or a receipt word inside a phrase (<c>שעות קבלה</c>,
        /// reception hours), is passed over.
        /// </summary>
        private static IsraeliDocumentType DetectDocumentType(PdfFieldExtractor.Prepared prepared)
        {
            foreach (var kl in prepared.Lines)
            {
                var words = TitleWordsOf(kl);
                foreach (var (type, phrases) in TitleWords)
                    foreach (string[] phrase in phrases)
                        for (int at = 0; at + phrase.Length <= words.Count; at++)
                            if (IsRunAt(words, phrase, at) && StandsAsTitle(words, at, at + phrase.Length)) return type;
            }

            foreach (var kl in prepared.Lines)
            {
                var words = TitleWordsOf(kl);
                for (int at = 0; at < words.Count; at++)
                    if (ReceiptWordKeys.Contains(words[at].Word) && StandsAsTitle(words, at, at + 1))
                        return IsraeliDocumentType.Receipt;
            }
            return IsraeliDocumentType.Unknown;
        }

        /// <summary>
        /// One word of a line as a title is read: a label key between joining punctuation, the
        /// segment of the line its word is in (<see cref="PdfFieldExtractor.Segments"/>), where it
        /// lies in the line's key, and whether it ends a sentence — it is its word's last, and the
        /// word ends in a full stop.
        /// </summary>
        private sealed class TitleWord
        {
            internal string Word;
            internal int Segment;
            internal int Start;
            internal int End;
            internal bool EndsWithStop;
        }

        // A line's words in reading order, as label keys split at the joining punctuation.
        private static List<TitleWord> TitleWordsOf(PdfFieldExtractor.KeyedLine kl)
        {
            int[] segmentOf = PdfFieldExtractor.Segments(kl);
            var words = new List<TitleWord>();
            int start = -1;
            for (int i = 0; i <= kl.Key.Length; i++)
            {
                bool wordEnds = i == kl.Key.Length || (i > 0 && kl.WordOf[i] != kl.WordOf[i - 1]);
                if (start >= 0 && (wordEnds || IsTitleJoiner(kl.Key[i])))
                {
                    words.Add(new TitleWord { Word = kl.Key.Substring(start, i - start), Segment = segmentOf[kl.WordOf[start]], Start = start, End = i });
                    start = -1;
                }
                if (start < 0 && i < kl.Key.Length && !IsTitleJoiner(kl.Key[i])) start = i;
            }

            for (int k = 0; k < words.Count; k++)
            {
                int word = kl.WordOf[words[k].Start];
                if (k + 1 < words.Count && kl.WordOf[words[k + 1].Start] == word) continue;
                int end = words[k].End;
                while (end < kl.Key.Length && kl.WordOf[end] == word) end++;
                words[k].EndsWithStop = kl.Key[end - 1] == '.';
            }
            return words;
        }

        /// <summary>
        /// Whether the title words [<paramref name="from"/>, <paramref name="to"/>) of a line stand
        /// as its title rather than a sentence's words. In the segment of the line its first word is
        /// in, no word with letters comes before them but a copy's marks (<c>העתק נאמן למקור
        /// חשבונית מס</c>); in the segment of its last, at most <see cref="MaxWordsAfterTitle"/>
        /// follow, number words and copy marks aside (<c>חשבונית מס לחודש ינואר 2026</c>), and no
        /// word with letters and a full stop ends it (<c>חשבונית מס חייבת במע"מ.</c>). Its own words
        /// may stand apart, as a spaced-out title's do. The segments are walked from the title out,
        /// so a sentence naming a title stops the walk at its first word.
        /// </summary>
        private static bool StandsAsTitle(List<TitleWord> words, int from, int to)
        {
            int segment = words[from].Segment;
            for (int k = from - 1; k >= 0 && words[k].Segment == segment; k--)
                if (HasLetter(words[k].Word) && !IsCopyMark(words[k].Word)) return false;

            segment = words[to - 1].Segment;
            int after = 0, end = to;
            for (; end < words.Count && words[end].Segment == segment; end++)
            {
                string w = words[end].Word;
                if (HasLetter(w) && !IsCopyMark(w) && !NumberWordKeys.Contains(w) && ++after > MaxWordsAfterTitle) return false;
            }
            var last = words[end - 1];
            return !(last.EndsWithStop && HasLetter(last.Word) && !NumberWordKeys.Contains(last.Word));
        }

        // Whether a title-number match's label stands as its line's title.
        private static bool StandsAsTitle(PdfFieldExtractor.Candidate c)
        {
            var words = TitleWordsOf(c.Line);
            int from = words.FindIndex(w => w.Start >= c.Span.At);
            int to = words.FindLastIndex(w => w.End <= c.Span.At + c.Span.Length) + 1;
            return from >= 0 && to > from && StandsAsTitle(words, from, to);
        }

        private static bool HasLetter(string word) => word.Any(char.IsLetter);

        // A copy's mark, in brackets or not: "(העתק)".
        private static bool IsCopyMark(string word) => CopyMarkKeys.Contains(word.Trim('(', ')', '[', ']'));

        private static void Flush(List<string> words, System.Text.StringBuilder current)
        {
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }

        // A phrase's words, as TitleWordsOf would read them.
        private static string[] PhraseWords(string phrase)
        {
            var words = new List<string>();
            foreach (string part in phrase.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                var current = new System.Text.StringBuilder();
                foreach (char ch in InvoiceValueParser.LabelKey(part))
                {
                    if (IsTitleJoiner(ch)) Flush(words, current);
                    else current.Append(ch);
                }
                Flush(words, current);
            }
            return words.ToArray();
        }

        // Punctuation a title's words are joined with: "/", "\", "|", ".", ",", and every dash —
        // hyphen, en and em dash alike (InvoiceValueParser leaves en and em dashes unfolded, since
        // between numbers they mean a range).
        private static bool IsTitleJoiner(char ch)
            => ch == '/' || ch == '\\' || ch == '|' || ch == '.' || ch == ','
               || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.DashPunctuation;

        // Whether the line's title words at `at` are the phrase's words.
        private static bool IsRunAt(List<TitleWord> words, string[] run, int at)
        {
            if (run.Length == 0) return false;
            for (int k = 0; k < run.Length; k++)
                if (words[at + k].Word != run[k]) return false;
            return true;
        }

        private static bool IsHebrewLetter(char c) => c >= '\u05D0' && c <= '\u05EA';

        /// <summary>How many lines after a customer cue its number may be printed (name and address between).</summary>
        private const int CustomerBlockLines = 5;

        private static void ReadIdentifiers(IsraeliInvoice invoice, PdfFieldExtractor.Prepared prepared, FieldQuery query)
        {
            // In reading order, so the cue at or above a line is found by binary search.
            var cues = prepared.Lines.Where(IsCustomerCue).ToList();
            var cueOrders = cues.Select(c => c.Order).ToList();

            string supplier = null, customer = null, firstOther = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in prepared.All(query))
            {
                string id = IdentifierIn(m.Result.Value);
                // One number written with and without its leading zero is one number: the key is
                // the padded form the validator compares.
                if (id == null || !seen.Add(IsraeliIdentifiers.TryNormalize(id, out string key) ? key : id)) continue;

                // The nearest cue at or above this line on its page claims the first number under
                // it — while no customer is known, so one cue claims one number, and a number
                // under a later cue is no second customer.
                if (customer == null)
                {
                    int at = cueOrders.BinarySearch(m.Line.Order);
                    if (at < 0) at = ~at - 1;
                    var cue = at >= 0 ? cues[at] : null;
                    if (cue != null && ReferenceEquals(cue.Page, m.Line.Page)
                        && m.Line.Order - cue.Order <= CustomerBlockLines
                        && !InContactBlock(m)
                        && !UnderIssuersName(cue, m))
                    {
                        customer = id;
                        continue;
                    }
                }
                if (supplier == null) supplier = id;
                else firstOther ??= id;
            }

            // With no cue, a second valid number is taken as the customer's.
            if (customer == null && firstOther != null && IsraeliIdentifiers.IsValid(firstOther))
                customer = firstOther;

            invoice.SupplierId = supplier;
            invoice.CustomerId = customer;
        }

        // The words of an issuer's contact line, as keys with their closing dots and quotes trimmed:
        // a post-office box, a telephone, a fax.
        private static readonly HashSet<string> ContactWordKeys = new HashSet<string>(
            new[] { "ת.ד", "ת\"ד", "טל", "טלפון", "פקס", "tel", "phone", "fax" }.Select(ContactKey),
            StringComparer.Ordinal);

        private static string ContactKey(string word) => InvoiceValueParser.LabelKey(word).TrimEnd('.', '\'', '"');

        /// <summary>
        /// Whether an identifier's label stands in an issuer's contact block — <c>ת.ד. 100 חדרה,
        /// ח.פ. מס׳ 514286558 טל׳: 03-… פקס: 04-…</c>: its line's segment (<see
        /// cref="PdfFieldExtractor.Segments"/>) holds a word of a post-office box, a telephone or a
        /// fax. A number printed there is the issuer's, though a toll road prints the line under the
        /// customer's block, within a cue's reach; the cue does not claim it. A telephone a column
        /// away — the customer's block's own <c>טל: -</c> beside its <c>מס' תאגיד:</c> — is another
        /// block's.
        /// </summary>
        private static bool InContactBlock(PdfFieldExtractor.Candidate m)
        {
            var words = m.Line.Line.Words;
            int[] segmentOf = PdfFieldExtractor.Segments(m.Line);
            var labelSegments = new HashSet<int>();
            for (int i = 0; i < words.Count; i++)
                if (m.Own.Words.Contains(words[i])) labelSegments.Add(segmentOf[i]);
            for (int i = 0; i < words.Count; i++)
                if (labelSegments.Contains(segmentOf[i]) && IsContactWord(PdfFieldExtractor.WordReadingOrder(words[i])))
                    return true;
            return false;
        }

        // "טל" alone is a name (Tal); the telephone's abbreviation carries its geresh or a colon.
        private static bool IsContactWord(string word)
        {
            string key = ContactKey(word);
            return ContactWordKeys.Contains(key) && (key != ContactKey("טל") || word.Trim().Length > 2);
        }

        private static readonly string AddresseeKey = InvoiceValueParser.LabelKey("לכבוד");
        private static readonly HashSet<string> CompanySuffixKeys = new HashSet<string>(
            new[] { "בע\"מ", "בעמ", "ltd", "ltd.", "inc", "inc." }.Select(InvoiceValueParser.LabelKey), StringComparer.Ordinal);

        /// <summary>
        /// Whether an identifier's label stands under the issuer's name on the cue's own line: a
        /// parking operator prints its name, the customer's block (<c>לכבוד: …</c>) and the date side
        /// by side, and its own number in the column under its name, the line after. The issuer's
        /// name is a block before the addressee's cue in reading order that ends a company's name
        /// (<c>בע"מ</c>, Ltd). Only the addressee's <c>לכבוד</c> opens such a line; a customer word
        /// labelling an account number (<c>כרטיס לקוח:</c>) beside the customer's own name does not.
        /// </summary>
        private static bool UnderIssuersName(PdfFieldExtractor.KeyedLine cue, PdfFieldExtractor.Candidate m)
        {
            var words = cue.Line.Words;
            int[] segmentOf = PdfFieldExtractor.Segments(cue);
            int addressee = -1;
            for (int i = 0; i < words.Count && addressee < 0; i++)
                if (InvoiceValueParser.LabelKey(PdfFieldExtractor.WordReadingOrder(words[i])) == AddresseeKey) addressee = i;
            if (addressee < 0) return false;

            float cueX = words[addressee].X;
            for (int s = 0; s <= segmentOf.DefaultIfEmpty(0).Max(); s++)
            {
                if (s == segmentOf[addressee]) continue;
                var block = Enumerable.Range(0, words.Count).Where(i => segmentOf[i] == s && words[i].Chars.Count > 0).ToList();
                if (block.Count == 0) continue;
                // Before the cue in reading order: right of it on a right-to-left line, left of it otherwise.
                bool before = cue.Rtl ? words[block[0]].X > cueX : words[block[0]].X < cueX;
                if (!before || !block.Any(i => CompanySuffixKeys.Contains(
                        InvoiceValueParser.LabelKey(PdfFieldExtractor.WordReadingOrder(words[i]))))) continue;
                float left = block.Min(i => words[i].X), right = block.Max(i => words[i].X + words[i].Width);
                if (m.Own.Left < right && m.Own.Right > left) return true;
            }
            return false;
        }

        private static bool IsCueWord(PdfTextWord word)
        {
            string w = InvoiceValueParser.LabelKey(PdfFieldExtractor.WordReadingOrder(word));
            return CustomerCueWords.Any(c => w == InvoiceValueParser.LabelKey(c));
        }

        private static bool IsCustomerCue(PdfFieldExtractor.KeyedLine kl)
        {
            if (NotCustomerCuePhrases.Any(p => kl.Key.Contains(InvoiceValueParser.LabelKey(p)))) return false;
            return kl.Line.Words.Any(IsCueWord)
                   || CustomerCuePhrases.Any(p => kl.Key.Contains(InvoiceValueParser.LabelKey(p)));
        }

        /// <summary>
        /// The identification number in a value: of <see cref="IsraeliIdentifiers.Candidates"/>'
        /// runs (groups joined by a hyphen or a space), the first that passes the check digit,
        /// else the first; a run a Latin letter touches is a foreign number (<c>DE123456789</c>,
        /// <c>NL123456789B01</c>) and is returned whole, as a last resort — so the validator, which
        /// checks only Israeli-shaped numbers, does not fail its check digit. An <c>IL</c> prefix
        /// is Israel's own and is dropped.
        /// </summary>
        internal static string IdentifierIn(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string t = InvoiceValueParser.NormalizePunctuation(value);
            string first = null, foreign = null;
            foreach (var (index, length) in IsraeliIdentifiers.Candidates(t, spaceJoins: true))
            {
                string raw = t.Substring(index, length);
                bool before = index > 0 && IsLatinLetter(t[index - 1]);
                bool after = index + length < t.Length && IsLatinLetter(t[index + length]);
                bool israeliPrefix = before && !after && index >= 2
                                     && string.Compare(t, index - 2, "IL", 0, 2, StringComparison.OrdinalIgnoreCase) == 0
                                     && (index == 2 || !IsLatinLetter(t[index - 3]));
                if ((before || after) && !israeliPrefix)
                {
                    foreign ??= TokenAround(t, index, length);
                    continue;
                }
                if (IsraeliIdentifiers.IsValid(raw)) return raw;
                first ??= raw;
            }
            return first ?? foreign;
        }

        private static bool IsLatinLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        // The whole token a run sits in: the Latin letters and digits on either side of it.
        private static string TokenAround(string t, int index, int length)
        {
            int from = index, to = index + length;
            while (from > 0 && (IsLatinLetter(t[from - 1]) || char.IsDigit(t[from - 1]))) from--;
            while (to < t.Length && (IsLatinLetter(t[to]) || char.IsDigit(t[to]))) to++;
            return t.Substring(from, to - from);
        }

        /// <summary>
        /// The allocation number in a value. The Tax Authority's number is the last nine digits of
        /// a long confirmation number (24 to 26 digits, opening with the request's date and time),
        /// which invoices print whole, bold-tailed, or split into two runs that text extraction
        /// may return in either order — the run opening with the year is the head. Otherwise a
        /// run of exactly nine digits; otherwise the longest run of six or more, as printed, for
        /// the validator to report.
        /// </summary>
        // Under the value parser's cap and with a timeout, as every pattern over a file's text runs.
        private static readonly Regex DigitRuns = new Regex("[0-9]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        internal static string AllocationNumberIn(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > InvoiceValueParser.MaxValueLength) return null;
            List<string> runs;
            try
            {
                runs = DigitRuns.Matches(InvoiceValueParser.NormalizeDigits(value))
                    .Cast<Match>().Select(m => m.Value).Where(r => r.Length >= 6).ToList();
            }
            catch (RegexMatchTimeoutException) { return null; }
            if (runs.Count == 0) return null;

            string full = runs.FirstOrDefault(r => r.Length >= 18);
            if (full == null && runs.Count >= 2 && runs[0].Length + runs[1].Length >= 18)
                full = runs[1].StartsWith("20", StringComparison.Ordinal) && !runs[0].StartsWith("20", StringComparison.Ordinal)
                    ? runs[1] + runs[0]
                    : runs[0] + runs[1];
            if (full != null) return full.Substring(full.Length - 9);

            return runs.FirstOrDefault(r => r.Length == 9) ?? runs.OrderByDescending(r => r.Length).First();
        }

        // The first whitespace-separated token holding a digit, without surrounding punctuation.
        private static string FirstToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            foreach (string token in InvoiceValueParser.NormalizeDigits(value).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.Trim('#', ':', '.', ',', '(', ')', '[', ']');
                if (t.Any(char.IsDigit)) return t;
            }
            return null;
        }

        // A document number after a title: its first token with a digit, when that is no date.
        private static string DocumentNumberIn(string value)
        {
            string token = FirstToken(value);
            return token == null || InvoiceValueParser.TryFindDate(token, out _) ? null : token;
        }

        private static DateTime? FirstDateOnFirstPage(PdfFieldExtractor.Prepared prepared)
        {
            var first = prepared.Lines.FirstOrDefault()?.Page;
            foreach (var kl in prepared.Lines)
            {
                if (!ReferenceEquals(kl.Page, first)) break;
                if (InvoiceValueParser.TryFindDate(kl.Line.Text, out DateTime d)) return d;
            }
            return null;
        }

        // ----------------------------------------------------------------
        //  An embedded structured invoice
        // ----------------------------------------------------------------

        /// <summary>
        /// Takes the embedded invoice's values where it states them — they are data, the page is a
        /// picture of data — and reports where the two disagree.
        /// </summary>
        /// <remarks>
        /// <para><b>A credit note states its amounts positive</b> (EN 16931); a credit invoice's
        /// page commonly prints them negative. When the page's amounts are negative on a credit
        /// note, the XML's are compared and taken negated, so a document that agrees with itself
        /// is not reported.</para>
        /// <para><b>An Israeli VAT number in the XML carries the country prefix</b>
        /// (<c>IL514286558</c>, as EN 16931 requires); it is taken without it, so the identifier
        /// and allocation checks still apply.</para>
        /// </remarks>
        private static void ApplyEmbedded(IsraeliInvoice invoice, EmbeddedInvoice e)
        {
            invoice.EmbeddedInvoice = e;
            invoice.ExtractionFindings.Add(new InvoiceFinding(
                InvoiceFindingSeverity.Info, InvoiceFindingCodes.EmbeddedStructuredInvoice,
                $"Values were taken from the embedded {e.Syntax} invoice '{e.FileName}'."));

            bool credit = (e.TypeCode != null && CreditNoteTypeCodes.Contains(e.TypeCode.Trim()))
                          || invoice.DocumentType == IsraeliDocumentType.CreditInvoice;
            bool pageNegative = invoice.Total < 0 || invoice.Subtotal < 0 || invoice.Vat < 0;
            bool negate = credit && pageNegative;
            decimal? Signed(decimal? amount) => negate && amount > 0 ? -amount : amount;

            decimal? grandTotal = Signed(e.GrandTotal), taxTotal = Signed(e.TaxTotal), basis = Signed(e.TaxBasisTotal);

            Disagree(invoice, "total", invoice.Total, grandTotal);
            Disagree(invoice, "VAT", invoice.Vat, taxTotal);
            Disagree(invoice, "subtotal", invoice.Subtotal, basis);

            invoice.InvoiceNumber = e.InvoiceNumber ?? invoice.InvoiceNumber;
            invoice.Date = e.IssueDate ?? invoice.Date;
            invoice.Currency = e.Currency ?? invoice.Currency;
            invoice.Subtotal = basis ?? invoice.Subtotal;
            invoice.Vat = taxTotal ?? invoice.Vat;
            invoice.Total = grandTotal ?? invoice.Total;
            invoice.VatRate = e.TaxRatePercent ?? invoice.VatRate;
            invoice.SupplierId = WithoutIsraeliPrefix(e.SellerTaxId) ?? invoice.SupplierId;
            invoice.CustomerId = WithoutIsraeliPrefix(e.BuyerTaxId) ?? invoice.CustomerId;
            if (e.Lines.Count > 0)
                invoice.LineItems = e.Lines.Where(l => l != null).Select(l => new InvoiceLineItem
                {
                    Line = l.Line,
                    PageIndex = l.PageIndex,
                    CatalogNumber = l.CatalogNumber,
                    Description = l.Description,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    Discount = l.Discount,
                    Amount = Signed(l.Amount),
                }).ToList();
        }

        // "IL514286558" is Israel's own prefix on an Israeli number: the number is the digits.
        private static string WithoutIsraeliPrefix(string id)
        {
            if (id == null) return null;
            string t = id.Trim();
            if (t.Length > 2 && t.StartsWith("IL", StringComparison.OrdinalIgnoreCase)
                && IsraeliIdentifiers.TryNormalize(t.Substring(2), out _))
                return t.Substring(2).Trim();
            return id;
        }

        private static void Disagree(IsraeliInvoice invoice, string what, decimal? page, decimal? embedded)
        {
            if (page == null || embedded == null) return;
            bool agree;
            try { agree = Math.Abs(page.Value - embedded.Value) <= 0.01m; }
            catch (OverflowException) { agree = false; }   // too far apart to subtract: they disagree
            if (agree) return;
            invoice.ExtractionFindings.Add(new InvoiceFinding(
                InvoiceFindingSeverity.Warning, InvoiceFindingCodes.EmbeddedInvoiceMismatch,
                $"The page's {what} ({page.Value.ToString(CultureInfo.InvariantCulture)}) " +
                $"is not the embedded invoice's ({embedded.Value.ToString(CultureInfo.InvariantCulture)})."));
        }
    }
}
