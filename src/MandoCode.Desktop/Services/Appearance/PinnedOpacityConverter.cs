using Microsoft.UI.Xaml.Data;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Dims the pin glyph on models that are not pinned, so one control reads as both the current
/// state and the affordance to change it. Bound against the model name itself — the picker's items
/// are plain strings so the editable ComboBox keeps showing the model name in its text box.
/// </summary>
public sealed class PinnedOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        ModelOrdering.IsPinned(value as string) ? 1.0 : 0.3;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
