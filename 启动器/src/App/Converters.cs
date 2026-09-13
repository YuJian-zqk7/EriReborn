using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SetupLauncher.Core;

namespace SetupLauncher.App
{
    /// <summary>把 "#RRGGBB" 字符串转成画刷，用于每个软件的主题色。</summary>
    public sealed class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hex = value as string;
            if (string.IsNullOrEmpty(hex)) return Brushes.Gray;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                return brush;
            }
            catch { return Brushes.Gray; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>字符串非空则 Visible。</summary>
    public sealed class NonEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string;
            return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
