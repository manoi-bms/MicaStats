using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using Binding = System.Windows.Data.Binding;

namespace Kil0bitSystemMonitor.Services.Localization;

/// <summary>
/// Live XAML localization: <c>Text="{loc:Text 'Settings'}"</c>. The binding listens to the
/// singleton provider's language property, so already-open windows update immediately.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TextExtension : MarkupExtension
{
    public TextExtension(string english) => English = english;

    [ConstructorArgument("english")]
    public string English { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(UiTextProvider.Language))
        {
            Source = UiText.Provider,
            Mode = BindingMode.OneWay,
            Converter = UiTextConverter.Instance,
            ConverterParameter = English,
        }.ProvideValue(serviceProvider);

    private sealed class UiTextConverter : IValueConverter
    {
        public static UiTextConverter Instance { get; } = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            UiText.Tr(parameter as string ?? string.Empty);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
