using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Quartermaster.Gui.Localization;

/// <summary>Bind a XAML label to its localized source text.</summary>
public sealed class LocExtension : MarkupExtension
{
    public string Source { get; set; }
    public LocExtension(string source) => Source = source;
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizedText.Value)) { Source = new LocalizedText(Source), Mode = BindingMode.OneWay };
}
