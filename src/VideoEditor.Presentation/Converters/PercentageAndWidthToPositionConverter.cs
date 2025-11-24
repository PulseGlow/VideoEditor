using System;
using System.Globalization;
using System.Windows.Data;

namespace VideoEditor.Presentation.Converters
{
    /// <summary>
    /// 将百分比和总宽度转换为实际位置的多值转换器
    /// values[0]: percentage (double, 0-100)
    /// values[1]: totalWidth (double)
    /// </summary>
    public class PercentageAndWidthToPositionConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && 
                values[0] is double percentage && 
                values[1] is double totalWidth &&
                totalWidth > 0)
            {
                // 减去标记的一半宽度(6像素),使其居中对齐
                double offset = parameter is string offsetStr && offsetStr == "center" ? -6 : 0;
                return (percentage / 100.0) * totalWidth + offset;
            }
            return 0.0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}







