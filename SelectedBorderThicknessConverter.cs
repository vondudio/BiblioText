using BiblioText.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace BiblioText;

/// <summary>
/// Converts ImageItem.IsSelected to the Border.BorderThickness on the thumbnail
/// tile: 0 when not selected (no accent ring), 3 when selected.
/// </summary>
internal sealed class SelectedBorderThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool selected = value is bool b && b;
        return selected ? new Thickness(3) : new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
