using System;
using System.IO;
using System.Windows.Media.Imaging;

public class VersionItem
{
    public string Name { get; set; } = string.Empty;

    public string IconPath { get; set; } = string.Empty;

    public string VersionId { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public BitmapImage IconImage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IconPath))
                return null!;

            string projectPath = Directory.GetParent(
                AppDomain.CurrentDomain.BaseDirectory
            )?.Parent?.Parent?.Parent?.FullName ?? "";

            string imagePath = Path.Combine(
                projectPath,
                IconPath.Replace('/', Path.DirectorySeparatorChar)
            );

            if (!File.Exists(imagePath))
                return null!;

            BitmapImage bitmap = new BitmapImage();

            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
    }
}