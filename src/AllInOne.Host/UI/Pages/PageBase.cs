using System.Windows;
using System.Windows.Controls;
using AllInOne.Core.Install;
using AllInOne.Core.Modules;
using AllInOne.Ui;

namespace AllInOne.Host.UI.Pages;

/// <summary>Прокручиваемая страница с ограниченной шириной содержимого.</summary>
internal abstract class PageBase : UserControl, IPage
{
    protected PageBase()
    {
        Body = new StackPanel { MaxWidth = 860, Margin = new Thickness(32, 26, 32, 26) };
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Body,
        };
    }

    protected StackPanel Body { get; }

    protected static ModuleManager Manager => App.Current.Manager;

    public abstract void Refresh();

    public virtual void OnEntryChanged(ModuleEntry entry) => Refresh();

    /// <summary>Полоска прогресса текущей операции модуля или null, если операции нет.</summary>
    protected static FrameworkElement? ProgressFor(ModuleEntry entry)
    {
        if (!App.Current.Progress.TryGetValue(entry.Id, out var p)) return null;
        return ProgressView(p);
    }

    protected static FrameworkElement ProgressView(InstallProgress p)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(UiKit.Hint(p.Stage));
        panel.Children.Add(new ProgressBar
        {
            IsIndeterminate = p.Fraction is null,
            Minimum = 0,
            Maximum = 1,
            Value = p.Fraction ?? 0,
        });
        return panel;
    }
}
