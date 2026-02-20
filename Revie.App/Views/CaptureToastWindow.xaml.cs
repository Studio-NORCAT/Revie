using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Revie.App.Views;

public partial class CaptureToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(1900)
    };

    public CaptureToastWindow(string detailText)
    {
        InitializeComponent();
        DetailTextBlock.Text = string.IsNullOrWhiteSpace(detailText) ? "Saved successfully" : detailText;

        Loaded += OnLoaded;
        Closed += (_, _) => _closeTimer.Stop();
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            BeginCloseAnimation();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
        BeginOpenAnimation();
        _closeTimer.Start();
    }

    private void PositionBottomRight()
    {
        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        var workingArea = screen.WorkingArea;
        const int margin = 14;

        Left = workingArea.Right - ActualWidth - margin;
        Top = workingArea.Bottom - ActualHeight - margin;
    }

    private void BeginOpenAnimation()
    {
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ToastRoot.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void BeginCloseAnimation()
    {
        var fadeOut = new DoubleAnimation
        {
            From = ToastRoot.Opacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            Close();
        };
        ToastRoot.BeginAnimation(OpacityProperty, fadeOut);
    }
}
