using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FFXIII2SaveEditor;

// Automatic UI scaling. Reference size 1600x1000 logical units matches how the editor looks
// maximized on a 1080p screen at 100 % Windows scaling; on smaller
// windows or with Windows display scaling (125 %, 150 %...) the content is shrunk so it fits
// without clipped buttons, and on large windows it grows a little. Ctrl + mouse wheel,
// Ctrl +/- and Ctrl+0 adjust a manual zoom on top of the automatic one.
public partial class MainWindow
{
    private const double DesignWidth = 1600;
    private const double DesignHeight = 1000;
    private const double MinAutoScale = 0.62;
    private const double MaxAutoScale = 1.25;
    private double _userZoom = 1.0;

    private void InitUiScaling()
    {
        SourceInitialized += (_, _) => FitWindowToWorkArea();
        SizeChanged += (_, _) => ApplyUiScale();
        Loaded += (_, _) => ApplyUiScale();
        PreviewMouseWheel += UiScaling_PreviewMouseWheel;
        PreviewKeyDown += UiScaling_PreviewKeyDown;
        _userZoom = LoadUserZoom();
    }

    private static string ZoomFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FFXIII2SaveEditor", "zoom.txt");

    private static double LoadUserZoom()
    {
        try
        {
            if (File.Exists(ZoomFile) && double.TryParse(File.ReadAllText(ZoomFile).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                return Math.Clamp(z, 0.6, 1.6);
        }
        catch { }
        return 1.0;
    }

    private static void SaveUserZoom(double zoom)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ZoomFile)!);
            File.WriteAllText(ZoomFile, zoom.ToString("0.00", CultureInfo.InvariantCulture));
        }
        catch { }
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
        _userZoom = Math.Round(Math.Clamp(zoom, 0.6, 1.6), 2);
        ApplyUiScale();
        SaveUserZoom(_userZoom);
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
