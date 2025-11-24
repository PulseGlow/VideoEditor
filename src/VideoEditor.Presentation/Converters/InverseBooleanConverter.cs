using System;
using System.Globalization;
using System.Windows.Data;

namespace VideoEditor.Presentation.Converters
{
    /// <summary>
    /// 反转布尔值的转换器
    /// True -> False, False -> True
    /// </summary>
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }
}

