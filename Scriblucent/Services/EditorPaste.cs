using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>Chooses how a clipboard fragment enters a note. Keep formatting inserts the
/// source font and styles. Either text paste then takes the note's line spacing.</summary>
internal static class EditorPaste
{
    internal enum PasteAction
    {
        RichFragment,
        PlainText,
        Image
    }

    internal static bool HasRichFormatting(IDataObject data)
        => !string.IsNullOrEmpty(ReadText(data, DataFormats.Rtf))
            || !string.IsNullOrEmpty(ReadText(data, DataFormats.Html));

    internal static PasteAction Resolve(EditorPasteMode mode, IDataObject data)
    {
        if (mode == EditorPasteMode.KeepFormatting && HasRichFormatting(data))
            return PasteAction.RichFragment;
        if (IsImageData(data) && !HasText(data))
            return PasteAction.Image;
        return PasteAction.PlainText;
    }

    /// <summary>Text pasted into a note takes the note's line spacing. A lone picture does not.</summary>
    internal static bool ApplyNoteLineSpacing(EditorPasteMode mode, IDataObject data)
        => Resolve(mode, data) != PasteAction.Image;

    internal static bool TryPasteRich(RichTextBox editor, IDataObject data)
    {
        if (TryLoad(editor, data, DataFormats.Rtf, Encoding.ASCII))
            return true;
        return TryLoad(editor, data, DataFormats.Html, Encoding.UTF8);
    }

    internal static string? PlainText(IDataObject data)
        => ReadText(data, DataFormats.UnicodeText) ?? ReadText(data, DataFormats.Text);

    private static bool TryLoad(RichTextBox editor, IDataObject data, string format, Encoding encoding)
    {
        var text = ReadText(data, format);
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            using var stream = new MemoryStream(encoding.GetBytes(text));
            editor.Selection.Load(stream, format);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasText(IDataObject data)
        => !string.IsNullOrEmpty(PlainText(data));

    private static bool IsImageData(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.Bitmap)
                || data.GetDataPresent(DataFormats.Dib)
                || data.GetDataPresent(DataFormats.Tiff)
                || data.GetDataPresent("PNG");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static string? ReadText(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format))
                return null;
            return data.GetData(format) switch
            {
                string text => text,
                MemoryStream stream => ReadStream(stream),
                _ => null
            };
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static string ReadStream(MemoryStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
