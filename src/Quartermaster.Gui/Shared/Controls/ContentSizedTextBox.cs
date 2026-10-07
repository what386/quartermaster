using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Quartermaster.Gui.Shared;

/// <summary>Fits a single-line value while respecting the space left by adjacent controls.</summary>
public sealed class ContentSizedTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    static ContentSizedTextBox()
    {
        AffectsMeasure<ContentSizedTextBox>(TextProperty, PlaceholderTextProperty, PasswordCharProperty,
            RevealPasswordProperty, FontFamilyProperty, FontSizeProperty, FontStyleProperty, FontWeightProperty);
    }

    public ContentSizedTextBox()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        MaxWidth = 760;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Text ?? "";
        if (PasswordChar != default && !RevealPassword)
            text = new string(PasswordChar, text.Length);
        if (text.Length == 0)
            text = PlaceholderText ?? "";
        using var layout = new TextLayout(text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize, Foreground);
        var contentWidth = layout.WidthIncludingTrailingWhitespace + Padding.Left + Padding.Right
            + BorderThickness.Left + BorderThickness.Right + 4; // Leave room for the caret.
        var width = Math.Min(availableSize.Width, Math.Max(220, Math.Ceiling(contentWidth)));
        var measured = base.MeasureOverride(new Size(width, availableSize.Height));
        return new Size(width, measured.Height);
    }
}
