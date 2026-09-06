using Wpf.Ui.Controls;

namespace WinProvision.Store;

public sealed class CategoryChip
{
    public CategoryChip(string tag, string label, SymbolRegular icon, bool isSelected)
    {
        Tag = tag;
        Label = label;
        Icon = icon;
        IsSelected = isSelected;
    }

    public string Tag { get; }
    public string Label { get; }
    public SymbolRegular Icon { get; }
    public bool IsSelected { get; }
}
