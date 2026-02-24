using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Revie.App.Models;
using Revie.App.Native;

namespace Revie.App.Views;

public partial class OverlayWindow : Window
{
    private const double ToolbarInset = 14;
    private static readonly System.Windows.Media.Brush HitTestBackground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
    private static readonly System.Windows.Media.Brush TransparentBackground = System.Windows.Media.Brushes.Transparent;
    private readonly DrawingAttributes _drawingAttributes = new();
    private HwndSource? _hwndSource;
    private IntPtr _windowHandle;
    private bool _isDrawingMode;
    private bool _suppressToolbar;
    private PenSizeSettings _penSizeSettings;

    public OverlayWindow(PenSizeSettings penSizeSettings)
    {
        _penSizeSettings = penSizeSettings;
        InitializeComponent();

        _drawingAttributes.Color = Colors.Yellow;
        _drawingAttributes.Width = _penSizeSettings.Medium;
        _drawingAttributes.Height = _penSizeSettings.Medium;
        _drawingAttributes.FitToCurve = true;

        InkSurface.DefaultDrawingAttributes = _drawingAttributes;
        InkSurface.EditingMode = InkCanvasEditingMode.None;

        Loaded += (_, _) =>
        {
            ApplyVirtualScreenBounds();
            SetDrawingMode(false);
        };

        SizeChanged += (_, _) =>
        {
            UpdateToolbarConstraints();
            EnsureToolbarWithinBounds();
        };

        SourceInitialized += (_, _) =>
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(_windowHandle);
            _hwndSource?.AddHook(WndProc);
            ApplyVirtualScreenBounds();
            UpdateExtendedStyles();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Closed += (_, _) =>
        {
            if (_hwndSource is not null)
            {
                _hwndSource.RemoveHook(WndProc);
            }

            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        };
    }

    public bool IsDrawingMode => _isDrawingMode;

    public void ToggleDrawingMode()
    {
        SetDrawingMode(!_isDrawingMode);
    }

    public void SetDrawingMode(bool enabled)
    {
        _isDrawingMode = enabled;
        InkSurface.EditingMode = enabled ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        InkSurface.IsHitTestVisible = enabled;
        InkSurface.UseCustomCursor = enabled;
        OverlayRoot.Background = enabled ? HitTestBackground : TransparentBackground;
        InkSurface.Background = enabled ? HitTestBackground : TransparentBackground;
        ApplyToolbarVisibility();
        ApplyVirtualScreenBounds();
        UpdateExtendedStyles();

        if (enabled)
        {
            Dispatcher.BeginInvoke(new Action(PositionToolbarAtTopLeft));
            Topmost = false;
            Topmost = true;
            Activate();
            Focus();
        }
    }

    public void ClearStrokes()
    {
        InkSurface.Strokes.Clear();
    }

    public IDisposable SuppressToolbarForCapture()
    {
        var previous = _suppressToolbar;
        var previousOpacity = Opacity;
        var previousHitTest = IsHitTestVisible;
        _suppressToolbar = true;
        Opacity = 0;
        IsHitTestVisible = false;
        ApplyToolbarVisibility();
        UpdateLayout();
        return new ToolbarSuppression(this, previous, previousOpacity, previousHitTest);
    }

    public void ApplyPenSizeSettings(PenSizeSettings settings)
    {
        _penSizeSettings = settings;
        _drawingAttributes.Width = _penSizeSettings.Medium;
        _drawingAttributes.Height = _penSizeSettings.Medium;
        InkSurface.DefaultDrawingAttributes = _drawingAttributes;
    }

    public byte[] RenderOverlayPng()
    {
        var bounds = GetOverlayBoundsPixels();
        var pixelWidth = Math.Max(1, bounds.Width);
        var pixelHeight = Math.Max(1, bounds.Height);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var scaleX = InkSurface.ActualWidth <= 0 ? 1 : pixelWidth / InkSurface.ActualWidth;
            var scaleY = InkSurface.ActualHeight <= 0 ? 1 : pixelHeight / InkSurface.ActualHeight;
            context.PushTransform(new ScaleTransform(scaleX, scaleY));

            foreach (var stroke in InkSurface.Strokes)
            {
                stroke.Draw(context);
            }

            context.Pop();
        }

        var renderTarget = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        renderTarget.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderTarget));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public System.Drawing.Rectangle GetOverlayBoundsPixels()
    {
        if (_windowHandle != IntPtr.Zero && NativeMethods.GetWindowRect(_windowHandle, out var rect))
        {
            return new System.Drawing.Rectangle(rect.Left, rect.Top, Math.Max(1, rect.Width), Math.Max(1, rect.Height));
        }

        return System.Windows.Forms.SystemInformation.VirtualScreen;
    }

    private void ApplyVirtualScreenBounds()
    {
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                _windowHandle,
                IntPtr.Zero,
                virtualScreen.Left,
                virtualScreen.Top,
                virtualScreen.Width,
                virtualScreen.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateToolbarConstraints();
                EnsureToolbarWithinBounds();
            }));
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        Left = virtualScreen.Left / dpi.DpiScaleX;
        Top = virtualScreen.Top / dpi.DpiScaleY;
        Width = virtualScreen.Width / dpi.DpiScaleX;
        Height = virtualScreen.Height / dpi.DpiScaleY;
        UpdateToolbarConstraints();
        EnsureToolbarWithinBounds();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyVirtualScreenBounds();
            if (_isDrawingMode)
            {
                PositionToolbarAtTopLeft();
            }
        }));
    }

    private void UpdateExtendedStyles()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW;
        if (_isDrawingMode)
        {
            style &= ~NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            style |= NativeMethods.WS_EX_TRANSPARENT;
        }

        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));
        NativeMethods.SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST && !_isDrawingMode)
        {
            handled = true;
            return NativeMethods.HTTRANSPARENT;
        }

        return IntPtr.Zero;
    }

    private void ApplyToolbarVisibility()
    {
        var shouldShowToolbar = _isDrawingMode && !_suppressToolbar;
        ToolbarRoot.Visibility = shouldShowToolbar ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolbarConstraints();

        if (shouldShowToolbar)
        {
            PositionToolbarAtTopLeft();
            return;
        }

        EnsureToolbarWithinBounds();
    }

    private void PositionToolbarAtTopLeft()
    {
        if (!_isDrawingMode || ToolbarRoot.Visibility != Visibility.Visible)
        {
            return;
        }

        var primaryBounds = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
            ?? System.Windows.Forms.SystemInformation.VirtualScreen;
        var primaryTopLeftDip = PointFromScreen(new System.Windows.Point(primaryBounds.Left, primaryBounds.Top));
        SetToolbarPosition(primaryTopLeftDip.X + ToolbarInset, primaryTopLeftDip.Y + ToolbarInset);
    }

    private void UpdateToolbarConstraints()
    {
        if (ToolbarRoot is null)
        {
            return;
        }

        var availableWidth = Math.Max(180, ActualWidth - (ToolbarInset * 2));
        var availableHeight = Math.Max(140, ActualHeight - (ToolbarInset * 2));
        ToolbarRoot.MaxWidth = availableWidth;
        ToolbarRoot.MaxHeight = availableHeight;
    }

    private void EnsureToolbarWithinBounds()
    {
        if (ToolbarRoot.Visibility != Visibility.Visible)
        {
            return;
        }

        SetToolbarPosition(ToolbarRoot.Margin.Left, ToolbarRoot.Margin.Top);
    }

    private void SetToolbarPosition(double desiredLeft, double desiredTop)
    {
        if (ToolbarRoot.Visibility != Visibility.Visible)
        {
            return;
        }

        UpdateToolbarConstraints();
        ToolbarRoot.Measure(new System.Windows.Size(ToolbarRoot.MaxWidth, ToolbarRoot.MaxHeight));

        var toolbarWidth = Math.Min(ToolbarRoot.DesiredSize.Width, ToolbarRoot.MaxWidth);
        var toolbarHeight = Math.Min(ToolbarRoot.DesiredSize.Height, ToolbarRoot.MaxHeight);

        var maxX = Math.Max(ToolbarInset, ActualWidth - toolbarWidth - ToolbarInset);
        var maxY = Math.Max(ToolbarInset, ActualHeight - toolbarHeight - ToolbarInset);
        var clampedX = Math.Clamp(desiredLeft, ToolbarInset, maxX);
        var clampedY = Math.Clamp(desiredTop, ToolbarInset, maxY);

        ToolbarRoot.Margin = new Thickness(clampedX, clampedY, 0, 0);
    }

    private void ColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string colorText)
        {
            return;
        }

        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorText);
        _drawingAttributes.Color = color;
        InkSurface.EditingMode = InkCanvasEditingMode.Ink;
        EraserToggle.IsChecked = false;
    }

    private void SizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string sizeName)
        {
            return;
        }

        var value = sizeName switch
        {
            "Small" => _penSizeSettings.Small,
            "Medium" => _penSizeSettings.Medium,
            "Large" => _penSizeSettings.Large,
            _ => _penSizeSettings.Medium
        };

        _drawingAttributes.Width = value;
        _drawingAttributes.Height = value;
        InkSurface.EditingMode = InkCanvasEditingMode.Ink;
        EraserToggle.IsChecked = false;
    }

    private void EraserToggle_OnChecked(object sender, RoutedEventArgs e)
    {
        InkSurface.EditingMode = InkCanvasEditingMode.EraseByPoint;
    }

    private void EraserToggle_OnUnchecked(object sender, RoutedEventArgs e)
    {
        InkSurface.EditingMode = InkCanvasEditingMode.Ink;
    }

    private void ClearButton_OnClick(object sender, RoutedEventArgs e)
    {
        ClearStrokes();
    }

    private sealed class ToolbarSuppression : IDisposable
    {
        private readonly OverlayWindow _owner;
        private readonly bool _previousValue;
        private readonly double _previousOpacity;
        private readonly bool _previousHitTestVisible;
        private bool _disposed;

        public ToolbarSuppression(
            OverlayWindow owner,
            bool previousValue,
            double previousOpacity,
            bool previousHitTestVisible)
        {
            _owner = owner;
            _previousValue = previousValue;
            _previousOpacity = previousOpacity;
            _previousHitTestVisible = previousHitTestVisible;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner._suppressToolbar = _previousValue;
            _owner.Opacity = _previousOpacity;
            _owner.IsHitTestVisible = _previousHitTestVisible;
            _owner.ApplyToolbarVisibility();
            _owner.UpdateLayout();
        }
    }
}
