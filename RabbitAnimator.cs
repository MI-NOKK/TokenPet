using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TokenPet;

public sealed class RabbitAnimator
{
    readonly Image image;
    readonly ScaleTransform scale = new();
    readonly RotateTransform rotate = new();
    readonly TranslateTransform move = new();
    readonly Dictionary<string, BitmapImage> assets = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Stopwatch elapsed = new();
    double changedAt;
    double? remaining;
    bool dragging;
    bool automatic = true;
    bool greeting;
    RabbitAction? manualAction;
    RabbitAction action;
    public event Action<RabbitAction>? ActionChanged;
    public event Action<RabbitFrame>? FrameChanged;

    public RabbitAnimator(Image image)
    {
        this.image = image;
        foreach (var pair in new Dictionary<string, string> {
            ["idle"] = "usagi-v2-idle", ["wave"] = "usagi-v2-wave", ["jump"] = "usagi-v2-jump",
            ["walk"] = "usagi-v2-walk", ["sleep"] = "usagi-v2-sleep", ["prone"] = "usagi-v2-prone" })
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri($"pack://application:,,,/Assets/{pair.Value}.png");
            bitmap.DecodePixelWidth = 384;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            assets.Add(pair.Key, bitmap);
        }
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(rotate);
        transform.Children.Add(move);
        image.RenderTransform = transform;
        timer.Tick += (_, _) => Tick(elapsed.Elapsed.TotalSeconds - changedAt);
        Preview(RabbitAction.Idle, 0);
    }

    public void Start() { elapsed.Start(); timer.Start(); }
    public void Pause() { elapsed.Stop(); timer.Stop(); }

    public void SetQuota(double? value)
    {
        remaining = value;
        if (automatic && !dragging && !greeting) Set(value <= 10 ? RabbitAction.Sleep : RabbitAction.Idle);
    }

    public void Select(RabbitAction? selected)
    {
        automatic = !selected.HasValue;
        manualAction = selected;
        greeting = false;
        Set(selected ?? (remaining <= 10 ? RabbitAction.Sleep : RabbitAction.Idle));
    }

    public void Greet() { greeting = true; Set(RabbitAction.Wave); }
    public void BeginDrag() { dragging = true; greeting = false; Set(RabbitAction.Drag); }
    public void EndDrag() { dragging = false; Greet(); }

    void Set(RabbitAction next)
    {
        action = next;
        changedAt = elapsed.Elapsed.TotalSeconds;
        Apply(RabbitMotion.Sample(next, 0));
        ActionChanged?.Invoke(next);
    }

    void Tick(double age)
    {
        if (greeting && !dragging && age >= 3.8)
        {
            greeting = false;
            Set(manualAction ?? (remaining <= 10 ? RabbitAction.Sleep : RabbitAction.Idle));
            age = 0;
        }
        if (automatic && !greeting && !dragging && age >= (action is RabbitAction.Jump or RabbitAction.Wave ? 3.8 : 8))
        {
            var choices = remaining <= 10 ? new[] { RabbitAction.Sleep, RabbitAction.Prone } : remaining <= 25
                ? new[] { RabbitAction.Idle, RabbitAction.Sleep, RabbitAction.Prone, RabbitAction.Wave }
                : new[] { RabbitAction.Idle, RabbitAction.Walk, RabbitAction.Jump, RabbitAction.Wave, RabbitAction.Sleep, RabbitAction.Prone };
            var alternatives = choices.Where(choice => choice != action).ToArray();
            Set(alternatives.Length == 0 ? choices[0] : alternatives[Random.Shared.Next(alternatives.Length)]);
            age = 0;
        }
        Apply(RabbitMotion.Sample(action, age));
    }

    public void Preview(RabbitAction state, double seconds) => Apply(RabbitMotion.Sample(state, seconds));

    void CheckAssets()
    {
        foreach (var pair in assets)
        {
            var bitmap = new FormatConvertedBitmap(pair.Value, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var transparent = 0;
            var opaque = 0;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] == 0) transparent++;
                if (pixels[i] >= 250) opaque++;
            }
            var count = bitmap.PixelWidth * bitmap.PixelHeight;
            if (transparent < count / 10 || opaque < count / 20 || pixels[3] != 0)
                throw new Exception($"Rabbit asset must have transparent surroundings and an opaque character: {pair.Key}");
        }
    }

    public static void SelfTest()
    {
        var animator = new RabbitAnimator(new Image());
        animator.CheckAssets();
        animator.Select(RabbitAction.Sleep);
        animator.Greet();
        animator.Tick(4);
        if (animator.action != RabbitAction.Sleep) throw new Exception("Greeting must return to the selected action.");
        animator.BeginDrag();
        animator.Tick(20);
        if (animator.action != RabbitAction.Drag) throw new Exception("Automatic motion must not interrupt dragging.");
        animator.EndDrag();
        animator.Tick(4);
        if (animator.action != RabbitAction.Sleep) throw new Exception("Dragging must preserve the selected action.");
        animator.Select(null);
        animator.SetQuota(10);
        animator.Tick(9);
        if (animator.action is not (RabbitAction.Sleep or RabbitAction.Prone)) throw new Exception("Low quota must rest in automatic mode.");
        animator.SetQuota(80);
        animator.Tick(9);
        if (animator.action == RabbitAction.Idle) throw new Exception("Automatic play must switch actions.");
    }

    void Apply(RabbitFrame frame)
    {
        if (!ReferenceEquals(image.Source, assets[frame.Asset])) image.Source = assets[frame.Asset];
        scale.ScaleX = frame.ScaleX;
        scale.ScaleY = frame.ScaleY;
        rotate.Angle = frame.Angle;
        move.X = frame.X;
        move.Y = frame.Y;
        FrameChanged?.Invoke(frame);
    }
}
