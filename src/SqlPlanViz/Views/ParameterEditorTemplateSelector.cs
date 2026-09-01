using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SqlPlanViz.ViewModels;

namespace SqlPlanViz.Views;

/// <summary>
/// Picks the parameter strip's value-column template by <see cref="ParameterBindingItem.EditorKind"/>
/// (Phase 1 of docs/editor-and-parameters-ux-plan.md). Kinds with no dedicated template yet — Text,
/// Guid, Binary — fall back to <see cref="DefaultTemplate"/>, so later phases add a template here
/// without touching the row shape those kinds already share.
/// </summary>
public sealed class ParameterEditorTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; }

    public DataTemplate? NumericTemplate { get; set; }

    public DataTemplate? BitTemplate { get; set; }

    public DataTemplate? DateTimeTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => SelectTemplateCore(item, null!);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        item switch
        {
            ParameterBindingItem { EditorKind: ParameterEditorKind.Numeric } => NumericTemplate ?? DefaultTemplate,
            ParameterBindingItem { EditorKind: ParameterEditorKind.Bit } => BitTemplate ?? DefaultTemplate,
            ParameterBindingItem { EditorKind: ParameterEditorKind.DateTime } => DateTimeTemplate ?? DefaultTemplate,
            _ => DefaultTemplate,
        };
}
