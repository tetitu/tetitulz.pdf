// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.WpfViewer.Services;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Tetitulz.Pdf.WpfViewer.Views;

/// <summary>
/// The document's pages as thumbnails, to put in order: select several (Ctrl, Shift), drag them to
/// a new place, turn them, delete them, insert another PDF's pages, or extract a selection to a file
/// of its own. Nothing changes the document until OK, and then as one step undo can take back.
/// </summary>
public sealed class PageOrganizerWindow : Window
{
    private const int ThumbnailDpi = 18;

    /// <summary>One page as the organiser shows it.</summary>
    public sealed class PageItem : INotifyPropertyChanged
    {
        private int _rotation;
        private string _label = "";
        private ImageSource? _thumbnail;

        public required int SourcePage { get; init; }

        /// <summary>The page's <c>/Rotate</c> when the thumbnail was drawn: the thumbnail already shows it.</summary>
        public required int DrawnRotation { get; init; }

        /// <summary>The <c>/Rotate</c> the page will have.</summary>
        public int Rotation { get => _rotation; set { _rotation = value; Notify(); Notify(nameof(ExtraTurn)); } }

        /// <summary>How far the thumbnail is turned on screen, beyond what it was drawn with.</summary>
        public double ExtraTurn => ((Rotation - DrawnRotation) % 360 + 360) % 360;

        public string Label { get => _label; set { _label = value; Notify(); } }
        public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; Notify(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<PageItem> _pages = new();
    private readonly List<byte[]> _inserted = new();
    private readonly ListBox _list;
    private readonly int _ownPageCount;
    private readonly Func<IReadOnlyList<int>, Task> _extract;
    private Point _dragFrom;

    /// <summary>The pages in their new order and turn, when the user chose OK.</summary>
    public IReadOnlyList<OrganizedPage> Result => _pages.Select(p => new OrganizedPage(p.SourcePage, p.Rotation)).ToList();

    /// <summary>The PDFs inserted, in order: source page <c>PageCount + k</c> is their k-th page.</summary>
    public IReadOnlyList<byte[]> Inserted => _inserted;

    /// <param name="pdf">The document as shown (readable bytes).</param>
    /// <param name="rotations">Each page's <c>/Rotate</c>.</param>
    /// <param name="extract">Writes the given pages (0-based, the document's own) to a file the user chooses.</param>
    public PageOrganizerWindow(byte[] pdf, int[] rotations, Func<IReadOnlyList<int>, Task> extract)
    {
        _ownPageCount = rotations.Length;
        _extract = extract;
        Title = "Organize pages";
        Width = 980;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.MainWindow;
        ShowInTaskbar = false;

        _list = new ListBox
        {
            ItemsSource = _pages,
            SelectionMode = SelectionMode.Extended,
            AllowDrop = true,
            ItemTemplate = ThumbnailTemplate(),
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewMouseLeftButtonDown += (_, e) => _dragFrom = e.GetPosition(_list);
        _list.PreviewMouseMove += OnDragMove;
        _list.Drop += OnDrop;
        _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; } };

        var tools = new WrapPanel { Margin = new Thickness(8, 8, 8, 4) };
        tools.Children.Add(Tool("Turn left", () => Turn(-90)));
        tools.Children.Add(Tool("Turn right", () => Turn(90)));
        tools.Children.Add(Tool("Move earlier", () => Move(-1)));
        tools.Children.Add(Tool("Move later", () => Move(1)));
        tools.Children.Add(Tool("Delete", DeleteSelected));
        tools.Children.Add(Tool("Insert pages from a PDF…", InsertFile));
        tools.Children.Add(Tool("Extract the selected pages…", () => _ = ExtractSelected()));
        tools.Children.Add(new TextBlock
        {
            Text = "Select with Ctrl or Shift; drag to move; Delete removes.",
            Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        });

        var ok = new WpfButton { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        var cancel = new WpfButton { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(8, 3, 8, 3) };
        ok.Click += (_, _) =>
        {
            if (_pages.Count == 0) { MessageBox.Show(this, "A document keeps at least one page.", Title); return; }
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(tools);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;

        for (int i = 0; i < rotations.Length; i++)
            _pages.Add(new PageItem { SourcePage = i, DrawnRotation = rotations[i], Rotation = rotations[i] });
        Renumber();
        _ = RenderThumbnailsAsync(pdf, _pages.ToList());
    }

    private static DataTemplate ThumbnailTemplate()
    {
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(PageItem.Thumbnail)));
        image.SetValue(HeightProperty, 150.0);
        image.SetValue(MarginProperty, new Thickness(4));
        image.SetBinding(LayoutTransformProperty, new System.Windows.Data.Binding(nameof(PageItem.ExtraTurn)) { Converter = new TurnConverter() });

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PageItem.Label)));
        label.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(MarginProperty, new Thickness(6));
        stack.SetValue(WidthProperty, 140.0);
        stack.AppendChild(image);
        stack.AppendChild(label);
        return new DataTemplate { VisualTree = stack };
    }

    private sealed class TurnConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => new RotateTransform(value is double d ? d : 0);
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private static WpfButton Tool(string text, Action act)
    {
        var b = new WpfButton { Content = text, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2) };
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>Draws the thumbnails off the window's thread, one render session for the file.</summary>
    private async Task RenderThumbnailsAsync(byte[] pdf, IReadOnlyList<PageItem> items)
    {
        int offset = items.Count > 0 ? items.Min(i => i.SourcePage) : 0;
        await Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(pdf, writable: false);
                using var session = PdfRenderSession.Open(ms);
                for (int k = 0; k < items.Count; k++)
                {
                    byte[] png;
                    try { png = session.RenderPage(items[k].SourcePage - offset, ThumbnailDpi); }
                    catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or ArgumentException) { continue; }
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(png);
                    bmp.EndInit();
                    bmp.Freeze();
                    PageItem item = items[k];
                    Dispatcher.BeginInvoke(() => item.Thumbnail = bmp);
                }
            }
            catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or IOException)
            {
                // Thumbnails that cannot be drawn leave their cells empty; the pages are still there.
            }
        });
    }

    private void Renumber()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            PageItem p = _pages[i];
            string from = p.SourcePage < _ownPageCount ? $"page {p.SourcePage + 1}" : "inserted";
            p.Label = $"{i + 1}  ({from})";
        }
    }

    private List<PageItem> Selected() => _list.SelectedItems.Cast<PageItem>().OrderBy(p => _pages.IndexOf(p)).ToList();

    private void Turn(int degrees)
    {
        foreach (PageItem p in Selected()) p.Rotation = ((p.Rotation + degrees) % 360 + 360) % 360;
    }

    private void Move(int step)
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        var order = step < 0 ? selected : Enumerable.Reverse(selected).ToList();
        foreach (PageItem p in order)
        {
            int at = _pages.IndexOf(p), to = at + step;
            if (to < 0 || to >= _pages.Count || selected.Contains(_pages[to])) continue;
            _pages.Move(at, to);
        }
        Renumber();
    }

    private void DeleteSelected()
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        if (selected.Count == _pages.Count) { MessageBox.Show(this, "A document keeps at least one page.", Title); return; }
        foreach (PageItem p in selected) _pages.Remove(p);
        Renumber();
    }

    private void InsertFile()
    {
        var d = new OpenFileDialog { Filter = "PDF|*.pdf" };
        if (d.ShowDialog(this) != true) return;
        byte[] bytes = File.ReadAllBytes(d.FileName);
        int[] rotations;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var doc = PdfDocument.Open(ms);
            if (doc.IsEncrypted) { MessageBox.Show(this, "That PDF is password-protected. Open it, remove its password, save it, and insert the copy.", Title); return; }
            rotations = doc.Pages.Select(p => PdfPageRotation.Normalize(p.Rotate)).ToArray();
        }
        catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or IOException)
        {
            MessageBox.Show(this, "That file could not be read as a PDF: " + ex.Message, Title);
            return;
        }
        int first = _ownPageCount + _inserted.Sum(CountPages);
        _inserted.Add(bytes);
        int at = _list.SelectedIndex >= 0 ? _pages.IndexOf(Selected().Last()) + 1 : _pages.Count;
        var added = new List<PageItem>();
        for (int i = 0; i < rotations.Length; i++)
        {
            var item = new PageItem { SourcePage = first + i, DrawnRotation = rotations[i], Rotation = rotations[i] };
            _pages.Insert(at + i, item);
            added.Add(item);
        }
        Renumber();
        _ = RenderThumbnailsAsync(bytes, added);
    }

    private static int CountPages(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);
        return doc.Pages.Count;
    }

    private async Task ExtractSelected()
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        if (selected.Any(p => p.SourcePage >= _ownPageCount))
        {
            MessageBox.Show(this, "Only the document's own pages can be extracted here; inserted pages are part of it after OK.", Title);
            return;
        }
        await _extract(selected.Select(p => p.SourcePage).ToList());
    }

    // ── drag to reorder ──────────────────────────────────────────────────

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Vector moved = e.GetPosition(_list) - _dragFrom;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var selected = Selected();
        if (selected.Count == 0 || ItemAt(_dragFrom) is not PageItem grabbed || !selected.Contains(grabbed)) return;
        DragDrop.DoDragDrop(_list, new DataObject(typeof(List<PageItem>), selected), DragDropEffects.Move);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(List<PageItem>)) is not List<PageItem> moving) return;
        PageItem? target = ItemAt(e.GetPosition(_list));
        if (target != null && moving.Contains(target)) return;
        foreach (PageItem p in moving) _pages.Remove(p);
        int at = target == null ? _pages.Count : _pages.IndexOf(target);
        for (int i = 0; i < moving.Count; i++) _pages.Insert(at + i, moving[i]);
        _list.SelectedItems.Clear();
        foreach (PageItem p in moving) _list.SelectedItems.Add(p);
        Renumber();
    }

    private PageItem? ItemAt(Point point)
    {
        DependencyObject? hit = _list.InputHitTest(point) as DependencyObject;
        while (hit != null && hit is not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        return (hit as ListBoxItem)?.DataContext as PageItem;
    }
}
