using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SqlPlanViz.Views;

/// <summary>Every Gauge style on one page; launch with <c>--gallery</c>.</summary>
public sealed partial class GaugeGalleryPage : Page
{
    public GaugeGalleryPage() => InitializeComponent();

    public UIElement TitleBar => GalleryTitleBar;
}
