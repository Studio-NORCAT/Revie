using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Revie.App.ViewModels;

namespace Revie.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _noteSaveTimer;
    private readonly DispatcherTimer _noteSavedFeedbackTimer;
    private readonly DispatcherTimer _videoPositionTimer;
    private readonly DispatcherTimer _videoToolbarFadeTimer;
    private bool _isPanning;
    private bool _isVideoSeekDragging;
    private bool _isUpdatingVideoSeekSlider;
    private bool _isVideoPlaying;
    private bool _resumeVideoAfterSeek;
    private bool _isVideoToolbarHovered;
    private bool _isPreviewFullscreen;
    private readonly GridLength _defaultMainStatusRowHeight;
    private readonly GridLength _defaultMainSidebarColumnWidth;
    private readonly GridLength _defaultMainSplitterColumnWidth;
    private readonly GridLength _defaultTopToolbarRowHeight;
    private readonly GridLength _defaultFilterSummaryRowHeight;
    private readonly GridLength _defaultPreviewActionsRowHeight;
    private readonly GridLength _defaultNotesRowHeight;
    private System.Windows.Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private MainViewModel? _boundViewModel;

    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        _noteSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(550)
        };
        _noteSaveTimer.Tick += NoteSaveTimerOnTick;

        _noteSavedFeedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _noteSavedFeedbackTimer.Tick += (_, _) =>
        {
            _noteSavedFeedbackTimer.Stop();
            NoteSavedBadge.Visibility = Visibility.Collapsed;
            NoteSavedBadge.Opacity = 0;
        };

        _videoPositionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _videoPositionTimer.Tick += VideoPositionTimerOnTick;
        _videoToolbarFadeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1400)
        };
        _videoToolbarFadeTimer.Tick += VideoToolbarFadeTimerOnTick;

        _defaultMainStatusRowHeight = MainStatusRow.Height;
        _defaultMainSidebarColumnWidth = MainSidebarColumn.Width;
        _defaultMainSplitterColumnWidth = MainSplitterColumn.Width;
        _defaultTopToolbarRowHeight = MainTopToolbarRow.Height;
        _defaultFilterSummaryRowHeight = MainFilterSummaryRow.Height;
        _defaultPreviewActionsRowHeight = MainPreviewActionsRow.Height;
        _defaultNotesRowHeight = MainNotesRow.Height;

        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
    }

    public void ShowAndActivate()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            if (_isPreviewFullscreen)
            {
                SetPreviewFullscreen(false);
            }
            Hide();
            return;
        }

        _videoToolbarFadeTimer.Stop();
        _videoPositionTimer.Stop();
        base.OnClosing(e);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundViewModel = DataContext as MainViewModel;
        if (_boundViewModel is not null)
        {
            _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedCapture))
        {
            _noteSaveTimer.Stop();
            BeginPreviewLoadSequence();
        }
    }

    private void PreviewScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_boundViewModel?.SelectedCapture is null || _boundViewModel.IsPreviewLoading || _boundViewModel.SelectedCapture.IsVideo)
        {
            return;
        }

        var oldScale = _boundViewModel.PreviewScale;
        var zoomFactor = e.Delta > 0 ? 1.12 : 0.88;
        var newScale = Math.Clamp(oldScale * zoomFactor, 0.25, 8.0);

        if (Math.Abs(newScale - oldScale) < 0.0001)
        {
            return;
        }

        var pointerPosition = e.GetPosition(PreviewScrollViewer);
        var contentX = (pointerPosition.X + PreviewScrollViewer.HorizontalOffset) / oldScale;
        var contentY = (pointerPosition.Y + PreviewScrollViewer.VerticalOffset) / oldScale;

        _boundViewModel.PreviewScale = newScale;
        PreviewScrollViewer.UpdateLayout();
        PreviewScrollViewer.ScrollToHorizontalOffset(Math.Max(0, (contentX * newScale) - pointerPosition.X));
        PreviewScrollViewer.ScrollToVerticalOffset(Math.Max(0, (contentY * newScale) - pointerPosition.Y));

        e.Handled = true;
    }

    private void PreviewScrollViewer_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_boundViewModel?.SelectedCapture is null || _boundViewModel.IsPreviewLoading || _boundViewModel.SelectedCapture.IsVideo)
        {
            return;
        }

        _isPanning = true;
        _panStartPoint = e.GetPosition(PreviewScrollViewer);
        _panStartHorizontalOffset = PreviewScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = PreviewScrollViewer.VerticalOffset;
        PreviewScrollViewer.Cursor = System.Windows.Input.Cursors.SizeAll;
        PreviewScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewScrollViewer_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var currentPoint = e.GetPosition(PreviewScrollViewer);
        var deltaX = currentPoint.X - _panStartPoint.X;
        var deltaY = currentPoint.Y - _panStartPoint.Y;

        PreviewScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - deltaX);
        PreviewScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - deltaY);
    }

    private void PreviewScrollViewer_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopPanning();
    }

    private void PreviewScrollViewer_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        StopPanning();
    }

    private void PreviewContentGrid_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ShowVideoToolbar(restartAutoHide: true);
    }

    private void PreviewContentGrid_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        StartVideoToolbarFadeCountdown();
    }

    private void VideoToolbarBorder_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isVideoToolbarHovered = true;
        ShowVideoToolbar(restartAutoHide: false);
        _videoToolbarFadeTimer.Stop();
    }

    private void VideoToolbarBorder_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isVideoToolbarHovered = false;
        StartVideoToolbarFadeCountdown();
    }

    private void VideoToolbarFadeTimerOnTick(object? sender, EventArgs e)
    {
        _videoToolbarFadeTimer.Stop();
        if (_isVideoToolbarHovered || _isVideoSeekDragging || _boundViewModel?.IsSelectedCaptureVideo != true)
        {
            return;
        }

        FadeVideoToolbarTo(0.08);
    }

    private void ShowVideoToolbar(bool restartAutoHide)
    {
        if (_boundViewModel?.IsSelectedCaptureVideo != true || VideoToolbarBorder.Visibility != Visibility.Visible)
        {
            return;
        }

        FadeVideoToolbarTo(1.0);
        if (restartAutoHide)
        {
            StartVideoToolbarFadeCountdown();
        }
    }

    private void StartVideoToolbarFadeCountdown()
    {
        if (_boundViewModel?.IsSelectedCaptureVideo != true || _isVideoToolbarHovered || _isVideoSeekDragging)
        {
            return;
        }

        _videoToolbarFadeTimer.Stop();
        _videoToolbarFadeTimer.Start();
    }

    private void FadeVideoToolbarTo(double targetOpacity)
    {
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase()
        };
        VideoToolbarBorder.BeginAnimation(OpacityProperty, animation);
    }

    private void VideoFullscreenButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetPreviewFullscreen(!_isPreviewFullscreen);
    }

    private void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_isPreviewFullscreen)
        {
            return;
        }

        SetPreviewFullscreen(false);
        e.Handled = true;
    }

    private void ResetViewButton_OnClick(object sender, RoutedEventArgs e)
    {
        FitPreviewToViewport();
    }

    private void ApplyFiltersButton_OnClick(object sender, RoutedEventArgs e)
    {
        SearchToggleButton.IsChecked = false;
    }

    private async void NoteSaveTimerOnTick(object? sender, EventArgs e)
    {
        _noteSaveTimer.Stop();
        if (_boundViewModel is null || _boundViewModel.SelectedCapture is null)
        {
            return;
        }

        await _boundViewModel.PersistSelectedCaptureMetadataAsync();
    }

    private async void SaveNoteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_boundViewModel is null || _boundViewModel.SelectedCapture is null)
        {
            return;
        }

        var saved = await _boundViewModel.PersistSelectedCaptureMetadataAsync();
        if (!saved)
        {
            return;
        }

        ShowNoteSavedFeedback();
    }

    private void NoteEditorTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_boundViewModel?.SelectedCapture is null)
        {
            return;
        }

        NoteSavedBadge.Visibility = Visibility.Collapsed;
        NoteSavedBadge.Opacity = 0;
        _noteSavedFeedbackTimer.Stop();
        _noteSaveTimer.Stop();
        _noteSaveTimer.Start();
    }

    private void ShowNoteSavedFeedback()
    {
        NoteSavedBadge.Visibility = Visibility.Visible;
        NoteSavedBadge.Opacity = 1;

        var pulse = new DoubleAnimation
        {
            From = 1,
            To = 0.4,
            Duration = TimeSpan.FromMilliseconds(280),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2)
        };
        NoteSavedBadge.BeginAnimation(OpacityProperty, pulse);

        _noteSavedFeedbackTimer.Stop();
        _noteSavedFeedbackTimer.Start();
    }

    private void ResetPreviewViewport()
    {
        _boundViewModel?.ResetPreviewScale();

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            PreviewScrollViewer.ScrollToHorizontalOffset(0);
            PreviewScrollViewer.ScrollToVerticalOffset(0);
        }));
    }

    private void SetPreviewFullscreen(bool isFullscreen)
    {
        if (_isPreviewFullscreen == isFullscreen)
        {
            return;
        }

        _isPreviewFullscreen = isFullscreen;
        if (isFullscreen)
        {
            MainStatusRow.Height = new GridLength(0);
            MainSidebarColumn.Width = new GridLength(0);
            MainSplitterColumn.Width = new GridLength(0);
            MainTopToolbarRow.Height = new GridLength(0);
            MainFilterSummaryRow.Height = new GridLength(0);
            MainPreviewActionsRow.Height = new GridLength(0);
            MainNotesRow.Height = new GridLength(0);
            VideoFullscreenButton.Content = "\uE73F";
            VideoFullscreenButton.ToolTip = "Exit fullscreen preview";
        }
        else
        {
            MainStatusRow.Height = _defaultMainStatusRowHeight;
            MainSidebarColumn.Width = _defaultMainSidebarColumnWidth;
            MainSplitterColumn.Width = _defaultMainSplitterColumnWidth;
            MainTopToolbarRow.Height = _defaultTopToolbarRowHeight;
            MainFilterSummaryRow.Height = _defaultFilterSummaryRowHeight;
            MainPreviewActionsRow.Height = _defaultPreviewActionsRowHeight;
            MainNotesRow.Height = _defaultNotesRowHeight;
            VideoFullscreenButton.Content = "\uE740";
            VideoFullscreenButton.ToolTip = "Fullscreen preview";
        }

        ShowVideoToolbar(restartAutoHide: true);
        Dispatcher.BeginInvoke(new Action(FitPreviewToViewport), DispatcherPriority.Background);
    }

    private void BeginPreviewLoadSequence()
    {
        if (_boundViewModel is null)
        {
            return;
        }

        if (_boundViewModel.SelectedCapture?.IsVideo == true)
        {
            _boundViewModel.SetPreviewLoading(false);
            ConfigureVideoPreview(_boundViewModel.SelectedCapture.VideoPath);
            return;
        }

        ClearVideoPreview();
        ResetVideoSeekSlider();
        _boundViewModel.SetPreviewLoading(true);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _ = _boundViewModel.SelectedCapture?.PreviewImage;
            FitPreviewToViewport();
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _boundViewModel.SetPreviewLoading(false);
            }));
        }));
    }

    private void FitPreviewToViewport()
    {
        if (_boundViewModel?.SelectedCapture is null)
        {
            ResetPreviewViewport();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            PreviewScrollViewer.UpdateLayout();

            if (PreviewImage.Source is not System.Windows.Media.Imaging.BitmapSource source ||
                source.PixelWidth <= 0 ||
                source.PixelHeight <= 0)
            {
                ResetPreviewViewport();
                return;
            }

            var viewportWidth = PreviewScrollViewer.ViewportWidth;
            var viewportHeight = PreviewScrollViewer.ViewportHeight;
            if (viewportWidth <= 1 || viewportHeight <= 1)
            {
                viewportWidth = Math.Max(1, PreviewScrollViewer.ActualWidth - 20);
                viewportHeight = Math.Max(1, PreviewScrollViewer.ActualHeight - 20);
            }

            var fitScale = Math.Min(viewportWidth / source.PixelWidth, viewportHeight / source.PixelHeight);
            if (double.IsNaN(fitScale) || double.IsInfinity(fitScale) || fitScale <= 0)
            {
                fitScale = 1.0;
            }

            _boundViewModel.PreviewScale = Math.Clamp(fitScale, 0.1, 1.0);
            PreviewScrollViewer.UpdateLayout();
            PreviewScrollViewer.ScrollToHorizontalOffset(0);
            PreviewScrollViewer.ScrollToVerticalOffset(0);
        }));
    }

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        PreviewScrollViewer.Cursor = System.Windows.Input.Cursors.Arrow;
        PreviewScrollViewer.ReleaseMouseCapture();
    }

    private void VideoPositionTimerOnTick(object? sender, EventArgs e)
    {
        UpdateVideoSeekSliderFromPlayer();
    }

    private void UpdateVideoSeekSliderFromPlayer()
    {
        if (_isVideoSeekDragging || _isUpdatingVideoSeekSlider || VideoPreviewElement.Source is null)
        {
            return;
        }

        if (!VideoPreviewElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var durationSeconds = Math.Max(0.1, VideoPreviewElement.NaturalDuration.TimeSpan.TotalSeconds);
        var currentSeconds = Math.Clamp(VideoPreviewElement.Position.TotalSeconds, 0, durationSeconds);

        _isUpdatingVideoSeekSlider = true;
        try
        {
            VideoSeekSlider.Maximum = durationSeconds;
            VideoSeekSlider.Value = currentSeconds;
            VideoTimeTextBlock.Text = BuildVideoTimeText(TimeSpan.FromSeconds(currentSeconds), TimeSpan.FromSeconds(durationSeconds));
        }
        finally
        {
            _isUpdatingVideoSeekSlider = false;
        }
    }

    private void ResetVideoSeekSlider()
    {
        _isUpdatingVideoSeekSlider = true;
        try
        {
            VideoSeekSlider.Minimum = 0;
            VideoSeekSlider.Maximum = 1;
            VideoSeekSlider.Value = 0;
            VideoTimeTextBlock.Text = "00:00 / 00:00";
        }
        finally
        {
            _isUpdatingVideoSeekSlider = false;
        }
    }

    private void SeekVideoToSliderPosition()
    {
        if (VideoPreviewElement.Source is null || !VideoPreviewElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var durationSeconds = Math.Max(0.1, VideoPreviewElement.NaturalDuration.TimeSpan.TotalSeconds);
        var seekSeconds = Math.Clamp(VideoSeekSlider.Value, 0, durationSeconds);
        VideoPreviewElement.Position = TimeSpan.FromSeconds(seekSeconds);
        VideoTimeTextBlock.Text = BuildVideoTimeText(TimeSpan.FromSeconds(seekSeconds), TimeSpan.FromSeconds(durationSeconds));
    }

    private static string BuildVideoTimeText(TimeSpan current, TimeSpan total)
    {
        static string Format(TimeSpan value)
        {
            return value.TotalHours >= 1
                ? value.ToString(@"hh\:mm\:ss")
                : value.ToString(@"mm\:ss");
        }

        return $"{Format(current)} / {Format(total)}";
    }

    private void VideoSeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _videoToolbarFadeTimer.Stop();
        ShowVideoToolbar(restartAutoHide: false);
        _resumeVideoAfterSeek = _isVideoPlaying;
        if (VideoPreviewElement.Source is not null)
        {
            try
            {
                VideoPreviewElement.Pause();
            }
            catch
            {
                // Ignore preview pause failures during seek.
            }
        }

        _isVideoPlaying = false;
        _videoPositionTimer.Stop();
        _isVideoSeekDragging = true;
    }

    private void VideoSeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isVideoSeekDragging)
        {
            return;
        }

        SeekVideoToSliderPosition();
        _isVideoSeekDragging = false;
        if (_resumeVideoAfterSeek)
        {
            try
            {
                VideoPreviewElement.Play();
                _isVideoPlaying = true;
                _videoPositionTimer.Start();
            }
            catch
            {
                _isVideoPlaying = false;
            }
        }
        _resumeVideoAfterSeek = false;
        StartVideoToolbarFadeCountdown();
    }

    private void VideoSeekSlider_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isVideoSeekDragging)
        {
            return;
        }

        SeekVideoToSliderPosition();
        _isVideoSeekDragging = false;
        if (_resumeVideoAfterSeek)
        {
            try
            {
                VideoPreviewElement.Play();
                _isVideoPlaying = true;
                _videoPositionTimer.Start();
            }
            catch
            {
                _isVideoPlaying = false;
            }
        }
        _resumeVideoAfterSeek = false;
        StartVideoToolbarFadeCountdown();
    }

    private void VideoSeekSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVideoSeekSlider)
        {
            return;
        }

        SeekVideoToSliderPosition();
        ShowVideoToolbar(restartAutoHide: true);
    }

    private void ConfigureVideoPreview(string? videoPath)
    {
        try
        {
            _videoToolbarFadeTimer.Stop();
            _isVideoToolbarHovered = false;
            VideoPreviewElement.Stop();
            VideoPreviewElement.Source = null;
            _videoPositionTimer.Stop();
            ResetVideoSeekSlider();
            _isVideoPlaying = false;
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            {
                _boundViewModel?.SetStatus("Video preview unavailable: file not found.");
                return;
            }

            VideoPreviewElement.Source = new Uri(videoPath, UriKind.Absolute);
            VideoPreviewElement.Position = TimeSpan.Zero;
            VideoPreviewElement.Play();
            ShowVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore preview setup failures.
        }
    }

    private void ClearVideoPreview()
    {
        try
        {
            _videoToolbarFadeTimer.Stop();
            _isVideoToolbarHovered = false;
            _videoPositionTimer.Stop();
            VideoPreviewElement.Stop();
            VideoPreviewElement.Source = null;
            ResetVideoSeekSlider();
            _isVideoPlaying = false;
            VideoToolbarBorder.BeginAnimation(OpacityProperty, null);
            VideoToolbarBorder.Opacity = 1;
        }
        catch
        {
            // Ignore release failures.
        }
    }

    private void VideoPlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            VideoPreviewElement.Play();
            _isVideoPlaying = true;
            _videoPositionTimer.Start();
            ShowVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore playback failures.
        }
    }

    private void VideoPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            VideoPreviewElement.Pause();
            _isVideoPlaying = false;
            UpdateVideoSeekSliderFromPlayer();
            ShowVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore playback failures.
        }
    }

    private void VideoStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            VideoPreviewElement.Stop();
            _videoPositionTimer.Stop();
            ResetVideoSeekSlider();
            _isVideoPlaying = false;
            ShowVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore playback failures.
        }
    }

    private void VideoRestartButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            VideoPreviewElement.Stop();
            VideoPreviewElement.Position = TimeSpan.Zero;
            VideoPreviewElement.Play();
            _isVideoPlaying = true;
            _videoPositionTimer.Start();
            ShowVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore playback failures.
        }
    }

    private void VideoPreviewElement_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            VideoPreviewElement.Position = TimeSpan.Zero;
            VideoPreviewElement.Pause();
            _isVideoPlaying = false;
            UpdateVideoSeekSliderFromPlayer();
            _videoPositionTimer.Start();
            ShowVideoToolbar(restartAutoHide: true);
        }
        catch
        {
            // Ignore media-open handling failures.
        }
    }

    private void VideoPreviewElement_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _isVideoPlaying = false;
        _videoPositionTimer.Stop();
        UpdateVideoSeekSliderFromPlayer();
        ShowVideoToolbar(restartAutoHide: true);
    }

    private void VideoPreviewElement_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _videoToolbarFadeTimer.Stop();
        _videoPositionTimer.Stop();
        ResetVideoSeekSlider();
        _isVideoPlaying = false;
        VideoToolbarBorder.BeginAnimation(OpacityProperty, null);
        VideoToolbarBorder.Opacity = 1;
        _boundViewModel?.SetStatus($"Video preview failed: {e.ErrorException?.Message ?? "Unsupported media format."}");
    }
}
