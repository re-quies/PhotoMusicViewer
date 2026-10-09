using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PhotoMusicViewer.Services
{
    /// <summary>Reserve the actual mode-selector width; constrain only the right-hand
    /// viewport at tiny window sizes. No wrapping or change in footer height.</summary>
    public sealed class PhotoActionsWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            double width = values.Length > 0 && values[0] is double w ? w : 0;
            double selectors = values.Length > 1 && values[1] is double s ? s : 88;
            Thickness padding = values.Length > 2 && values[2] is Thickness p ? p : new Thickness(8, 0, 8, 0);
            double centerReserve = 20;
            if (parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double requested)) centerReserve = requested;
            return AvailableWidth(width, selectors, padding.Left, padding.Right, centerReserve);
        }
        internal static double AvailableWidth(double width, double selectors, double left, double right, double centerReserve = 20)
        {
            if (!double.IsFinite(width) || !double.IsFinite(selectors) || !double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(centerReserve)) return 0;
            return Math.Max(0, width - Math.Max(0, selectors) - Math.Max(0, left) - Math.Max(0, right) - Math.Max(0, centerReserve));
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
