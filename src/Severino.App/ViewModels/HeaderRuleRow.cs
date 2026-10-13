using CommunityToolkit.Mvvm.ComponentModel;

namespace Severino.App.ViewModels;

/// <summary>A header rule being edited: a name and a value, where an empty value removes the header.</summary>
public sealed partial class HeaderRuleRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Value { get; set; } = "";
}