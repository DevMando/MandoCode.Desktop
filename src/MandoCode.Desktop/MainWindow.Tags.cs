using MandoCode.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MandoCode.Desktop;

public sealed partial class MainWindow
{
    /// <summary>Shows the catalog for one management surface. Assignment is handled by the item's
    /// editor; this modal owns the reusable tag names exposed by that surface's filter dropdown.</summary>
    private async Task ShowTagManagerAsync(TagScope scope, string title)
    {
        var input = new TextBox
        {
            PlaceholderText = "New tag (for example: database)",
            MinWidth = 300,
        };
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 120,
            MaxHeight = 260,
        };
        var add = new Button { Content = "Add tag", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var remove = new Button { Content = "Remove selected" };

        void Refresh()
        {
            list.ItemsSource = _itemTags.GetTags(scope);
            remove.IsEnabled = list.SelectedItem is string;
        }

        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is string;
        add.Click += (_, _) =>
        {
            _itemTags.AddTag(scope, input.Text);
            input.Text = "";
            Refresh();
            input.Focus(FocusState.Programmatic);
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is string tag)
            {
                _itemTags.DeleteTag(scope, tag);
                Refresh();
            }
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Tags are available only on this page type. Removing a tag also removes it from items using it.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addRow.Children.Add(input);
        addRow.Children.Add(add);
        content.Children.Add(addRow);
        content.Children.Add(list);
        content.Children.Add(remove);
        Refresh();

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private static IEnumerable<string> SplitTags(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
