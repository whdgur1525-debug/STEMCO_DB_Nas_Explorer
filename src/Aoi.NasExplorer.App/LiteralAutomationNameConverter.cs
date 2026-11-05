using System.Globalization;
using System.Windows.Data;

namespace Aoi.NasExplorer.App;

public sealed class LiteralAutomationNameConverter : IValueConverter
{
    // WPF's CheckBox automation peer removes an access-key underscore even from an explicit Name.
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is string text?text.Replace("_","__",StringComparison.Ordinal):"";
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
