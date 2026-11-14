using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SlimPenHaptics.Ui;

// Icons and friendly names for other apps' exe files.
public static class Icons
{
    public static ImageSource? ForExe(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // "Adobe Illustrator" rather than "Illustrator.exe", when the file says so.
    public static string DisplayName(string path)
    {
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception)
        {
            // fall through to the file name
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
