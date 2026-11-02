using System.Windows.Controls;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>Settings combos store their persisted value in each item's Tag.</summary>
internal static class ComboTags
{
    /// <summary>Selects the item whose Tag matches; falls back to the first item.</summary>
    public static void Select(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    public static string Of(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : fallback;
}
