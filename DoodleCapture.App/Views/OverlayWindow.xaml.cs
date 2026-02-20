using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DoodleCapture.App.Models;
using DoodleCapture.App.Native;
using Forms = System.Windows.Forms;

namespace DoodleCapture.App.Views;

public partial class OverlayWindow : Window
{
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
            Dispatcher.BeginInvoke(new Action(PositionToolbarNearCursor));
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
        _suppressToolbar = true;
        ApplyToolbarVisibility();
        return new ToolbarSuppression(this, previous);
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
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        Left = virtualScreen.Left / dpi.DpiScaleX;
        Top = virtualScreen.Top / dpi.DpiScaleY;
        Width = virtualScreen.Width / dpi.DpiScaleX;
        Height = virtualScreen.Height / dpi.DpiScaleY;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyVirtualScreenBounds();
            if (_isDrawingMode)
            {
                PositionToolbarNearCursor();
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
        ToolbarRoot.Visibility = _isDrawingMode && !_suppressToolbar ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PositionToolbarNearCursor()
    {
        if (!_isDrawingMode || ToolbarRoot.Visibility != Visibility.Visible)
        {
            return;
        }

        ToolbarRoot.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = Math.Max(220, ToolbarRoot.DesiredSize.Width);
        var toolbarHeight = Math.Max(150, ToolbarRoot.DesiredSize.Height);

        var overlayBounds = GetOverlayBoundsPixels();
        var cursor = Forms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this);
        var xDip = (cursor.X - overlayBounds.Left + 16) / dpi.DpiScaleX;
        var yDip = (cursor.Y - overlayBounds.Top + 16) / dpi.DpiScaleY;

        var maxX = Math.Max(14, ActualWidth - toolbarWidth - 14);
        var maxY = Math.Max(14, ActualHeight - toolbarHeight - 14);
        var clampedX = Math.Clamp(xDip, 14, maxX);
        var clampedY = Math.Clamp(yDip, 14, maxY);
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
        private bool _disposed;

        public ToolbarSuppression(OverlayWindow owner, bool previousValue)
        {
            _owner = owner;
            _previousValue = previousValue;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner._suppressToolbar = _previousValue;
            _owner.ApplyToolbarVisibility();
        }
    }
}
