using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TokenPet;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test"))
            {
                Usage.SelfTest();
                RabbitMotion.SelfTest();
                _ = new Application();
                RabbitAnimator.SelfTest();
                PetWindow.SelfTest();
                return 0;
            }
            if (args.Contains("--probe"))
            {
                var quotas = Usage.ReadAsync(Usage.FindCodex() ?? throw new Exception("codex.exe not found")).GetAwaiter().GetResult();
                File.WriteAllText("probe.json", JsonSerializer.Serialize(quotas, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            var app = new Application();
            if (args.Contains("--motion-preview")) { SaveMotionPreview(); return 0; }
            PetView? requestedView = args.Contains("--details") ? PetView.Details : args.Contains("--pet-only") ? PetView.PetOnly : args.Contains("--summary") ? PetView.Summary : null;
            var screenshot = args.Contains("--screenshot");
            var window = new PetWindow(args.Contains("--demo") || screenshot, requestedView, loadSettings: !screenshot);
            if (args.Contains("--screenshot"))
            {
                var content = (FrameworkElement)window.Content;
                var bitmap = Render(content, (int)window.Width, (int)window.Height);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(requestedView switch { PetView.Details => "preview-details.png", PetView.PetOnly => "preview-pet-only.png", _ => "preview.png" });
                png.Save(file);
                return 0;
            }
            app.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Contains("--self-test") || args.Contains("--probe") || args.Contains("--screenshot") || args.Contains("--motion-preview"))
                File.WriteAllText("check-error.txt", ex.Message);
            else MessageBox.Show(ex.Message, "TokenPet", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    static RenderTargetBitmap Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    static void SaveMotionPreview()
    {
        var stage = new Grid { Background = new SolidColorBrush(Color.FromRgb(248, 247, 239)) };
        var image = new Image { Width = 184, Height = 184, RenderTransformOrigin = new Point(0.5, 0.85), Margin = new Thickness(0, 10, 0, 18), VerticalAlignment = VerticalAlignment.Bottom };
        var caption = new TextBlock { FontFamily = new FontFamily("Malgun Gothic"), FontSize = 13, Foreground = Brushes.DarkSlateGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        stage.Children.Add(image);
        stage.Children.Add(caption);
        var animator = new RabbitAnimator(image);
        var gif = new GifBitmapEncoder();
        var actions = Enum.GetValues<RabbitAction>().Where(a => a != RabbitAction.Drag).ToArray();
        var gallery = new DrawingVisual();
        using (var drawing = gallery.RenderOpen())
        {
            for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
            {
                var action = actions[actionIndex];
                caption.Text = "TokenPet · " + RabbitMotion.Label(action);
                for (var frame = 0; frame < 24; frame++)
                {
                    animator.Preview(action, frame / 12.0);
                    var bitmap = Render(stage, 280, 288);
                    gif.Frames.Add(BitmapFrame.Create(bitmap));
                    if (frame == 6) drawing.DrawImage(bitmap, new Rect(actionIndex * 280, 0, 280, 288));
                }
            }
        }
        SaveLoopingGif(gif, "rabbit-motions.gif");
        var galleryBitmap = new RenderTargetBitmap(actions.Length * 280, 288, 96, 96, PixelFormats.Pbgra32);
        galleryBitmap.Render(gallery);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(galleryBitmap));
        using var galleryFile = File.Create("rabbit-poses.png");
        png.Save(galleryFile);
        SaveCompactPreview();
    }

    static void SaveCompactPreview()
    {
        var window = new PetWindow(true, PetView.Summary, loadSettings: false);
        var content = (FrameworkElement)window.Content;
        var gif = new GifBitmapEncoder();
        foreach (var action in new[] { RabbitAction.Idle, RabbitAction.Wave, RabbitAction.Jump, RabbitAction.Walk, RabbitAction.Prone, RabbitAction.Sleep })
        {
            for (var i = 0; i < 24; i++)
            {
                window.PreviewMotion(action, i / 12.0);
                var bitmap = Render(content, (int)window.Width, (int)window.Height);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(236, 240, 235)), null, new Rect(0, 0, window.Width, window.Height));
                    drawing.DrawImage(bitmap, new Rect(0, 0, window.Width, window.Height));
                }
                var frame = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                frame.Render(visual);
                gif.Frames.Add(BitmapFrame.Create(frame));
            }
        }
        SaveLoopingGif(gif, "usagi-compact.gif");
    }

    static void SaveLoopingGif(GifBitmapEncoder encoder, string path)
    {
        // WPF's GIF encoder ignores animation metadata. Add timing and looping to
        // its encoded blocks; the actual frame pixels stay exactly as rendered.
        using var encoded = new MemoryStream();
        encoder.Save(encoded);
        var bytes = encoded.ToArray();
        var headerLength = 13 + ((bytes[10] & 128) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0);
        var offset = headerLength;
        var frames = 0;
        while (offset < bytes.Length)
        {
            var kind = bytes[offset++];
            if (kind == 0x3b) break;
            if (kind == 0x21)
            {
                var label = bytes[offset++];
                if (label == 0xf9)
                {
                    bytes[offset + 1] = (byte)((bytes[offset + 1] & ~28) | 8);
                    bytes[offset + 2] = 8; // Hundredths of a second per frame.
                    bytes[offset + 3] = 0;
                }
            }
            else if (kind == 0x2c)
            {
                var packed = bytes[offset + 8];
                offset += 9 + ((packed & 128) != 0 ? 3 * (1 << ((packed & 7) + 1)) : 0);
                offset++; // LZW code size before the image data sub-blocks.
                frames++;
            }
            else throw new InvalidDataException("Unexpected GIF block.");
            while (bytes[offset] != 0) offset += bytes[offset] + 1;
            offset++;
        }
        if (frames != encoder.Frames.Count) throw new InvalidDataException("GIF frame count mismatch.");
        using var output = File.Create(path);
        output.Write(bytes.AsSpan(0, headerLength));
        output.Write(new byte[] { 0x21, 0xff, 11 });
        output.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        output.Write(new byte[] { 3, 1, 0, 0, 0 });
        output.Write(bytes.AsSpan(headerLength));
    }
}
