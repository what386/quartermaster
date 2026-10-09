using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace Quartermaster.Gui.Localization;

/// <summary>Bind a XAML label to its localized source text.</summary>
public sealed class LocExtension : MarkupExtension
{
    public string Source { get; set; }
    public LocExtension(string source) => Source = source;
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(Localizer.Language))
        {
            Source = Localizer.Current,
            Mode = BindingMode.OneWay,
            // Avalonia holds binding sources weakly; use the singleton so labels survive collection.
            Converter = new FuncValueConverter<string, string>(_ => Localizer.Text(Source))
        };
}
