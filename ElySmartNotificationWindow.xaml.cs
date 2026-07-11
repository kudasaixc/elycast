using System.Windows;
using System.Windows.Media.Animation;
using Elysium_Cast_IPTV.Services.ElySmart;

namespace Elysium_Cast_IPTV;

public enum ElySmartToastAction { Optimize, Ignore, AlwaysIgnore }

public partial class ElySmartNotificationWindow : Window
{
    public HealthIssue Issue { get; }
    public event EventHandler<ElySmartToastAction>? ActionSelected;

    public ElySmartNotificationWindow(HealthIssue issue)
    {
        Issue = issue;
        InitializeComponent();
        TitleText.Text = issue.Title;
        DetailText.Text = issue.Detail;
        ActionText.Text = issue.SuggestedAction;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 18;
        Top = area.Bottom - ActualHeight - 18;
        Opacity = 0;
        var finalTop = Top;
        Top += 34;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        BeginAnimation(TopProperty, new DoubleAnimation(Top, finalTop, TimeSpan.FromMilliseconds(280))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Optimize_Click(object sender, RoutedEventArgs e) => Complete(ElySmartToastAction.Optimize);
    private void Ignore_Click(object sender, RoutedEventArgs e) => Complete(ElySmartToastAction.Ignore);
    private void AlwaysIgnore_Click(object sender, RoutedEventArgs e) => Complete(ElySmartToastAction.AlwaysIgnore);

    private void Complete(ElySmartToastAction action)
    {
        ActionSelected?.Invoke(this, action);
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
