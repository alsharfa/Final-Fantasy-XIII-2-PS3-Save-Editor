using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FFXIII2SaveEditor;

// Automatic UI scaling. The layout was designed for a 1280x900 logical window; on smaller
// windows or with Windows display scaling (125 %, 150 %...) the content is shrunk so it fits
// without clipped buttons, and on large windows it grows a little. Ctrl + mouse wheel,
// Ctrl +/- and Ctrl+0 adjust a manual zoom on top of the automatic one.
public partial class MainWindow
{
    private const double DesignWidth = 1280;
    private const double DesignHeight = 900;
    private const double MinAutoScale = 0.62;
    private const double MaxAutoScale = 1.30;
    private double _userZoom = 1.0;

    private void InitUiScaling()
    {
        SourceInitialized += (_, _) => FitWindowToWorkArea();
        SizeChanged += (_, _) => ApplyUiScale();
        Loaded += (_, _) => ApplyUiScale();
        PreviewMouseWheel += UiScaling_PreviewMouseWheel;
        PreviewKeyDown += UiScaling_PreviewKeyDown;
    }

    private void FitWindowToWorkArea()
    {
        Rect work = SystemParameters.WorkArea; // logical units, already DPI-aware
        if (Width > work.Width * 0.95 || Height > work.Height * 0.95)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void ApplyUiScale()
    {
        if (RootLayout is null || RootScale is null) return;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return;

        double auto = Math.Min(w / DesignWidth, h / DesignHeight);
        auto = Math.Clamp(auto, MinAutoScale, MaxAutoScale);
        double scale = Math.Clamp(auto * _userZoom, 0.5, 2.0);
        RootScale.ScaleX = scale;
        RootScale.ScaleY = scale;
    }

    private void SetUserZoom(double zoom)
    {
        _userZoom = Math.Clamp(zoom, 0.6, 1.6);
        ApplyUiScale();
        try { StatusText.Text = $"Zoom {Math.Round(_userZoom * 100)} %  (Ctrl+0 = auto)"; } catch { }
    }

    private void UiScaling_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetUserZoom(_userZoom + (e.Delta > 0 ? 0.05 : -0.05));
        e.Handled = true;
    }

    private void UiScaling_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        switch (e.Key)
        {
            case Key.OemPlus: case Key.Add: SetUserZoom(_userZoom + 0.05); e.Handled = true; break;
            case Key.OemMinus: case Key.Subtract: SetUserZoom(_userZoom - 0.05); e.Handled = true; break;
            case Key.D0: case Key.NumPad0: SetUserZoom(1.0); e.Handled = true; break;
        }
    }
}
