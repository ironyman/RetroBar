using System.Windows;
using System.Windows.Controls;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    public class TrayItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate IconTemplate { get; set; }
        public DataTemplate SeparatorTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is SeparatorPlaceholder) return SeparatorTemplate;
            return IconTemplate;
        }
    }
}
