using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TokenPet;

public enum PetView { Summary, PetOnly, Details }

public partial class PetWindow : Window
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(5) };
    readonly CancellationTokenSource closing = new();
    readonly RabbitAnimator animator;
    readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenPet", "settings.json");
    string? codexPath;
    bool demo;
    bool busy;
    DateTimeOffset? lastUpdated;
    PetView view = PetView.Summary;

    public PetWindow(bool demo, PetView? requestedView = null, bool loadSettings = true)
    {
        InitializeComponent();
        animator = new RabbitAnimator(RabbitImage);
        animator.ActionChanged += action => MotionLabel.Text = RabbitMotion.Label(action);
        animator.FrameChanged += frame => { BubbleMove.X = frame.X; BubbleMove.Y = frame.Y + frame.HeadOffset; };
        this.demo = demo;
        codexPath = Usage.FindCodex();
        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, work.Right - Width - 24);
        Top = Math.Max(work.Top, work.Bottom - Height - 24);
        try
        {
            if (loadSettings && File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("view", out var savedView) && savedView.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<PetView>(savedView.GetString(), out var parsedView) && Enum.IsDefined(parsedView)) view = parsedView;
                if (root.TryGetProperty("codexPath", out var path) && path.ValueKind == JsonValueKind.String && File.Exists(path.GetString())) codexPath = path.GetString();
                if (root.TryGetProperty("left", out var left) && left.TryGetDouble(out var x) && double.IsFinite(x)) Left = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - Width));
                if (root.TryGetProperty("top", out var top) && top.TryGetDouble(out var y) && double.IsFinite(y)) Top = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - Height));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
        SetView(requestedView ?? view, preserveFeet: false);

        var menu = new ContextMenu();
        Add("지금 새로고침", async () => await RefreshAsync());
        Add("연동 설정", ShowSettings);
        var viewMenu = new MenuItem { Header = "표시 모드" };
        menu.Items.Add(viewMenu);
        var viewItems = new Dictionary<PetView, MenuItem>();
        foreach (var mode in Enum.GetValues<PetView>())
        {
            var item = new MenuItem { Header = mode switch { PetView.Summary => "펫 + 머리 위 요약", PetView.PetOnly => "펫만 표시", _ => "상세 카드 + 펫" }, IsCheckable = true, IsChecked = view == mode };
            item.Click += (_, _) =>
            {
                SetView(mode);
                foreach (var pair in viewItems) pair.Value.IsChecked = pair.Key == mode;
                SaveSettings();
            };
            viewItems.Add(mode, item);
            viewMenu.Items.Add(item);
        }
        var motionMenu = new MenuItem { Header = "우사기 움직임" };
        menu.Items.Add(motionMenu);
        var automatic = new MenuItem { Header = "자동으로 놀기", IsCheckable = true, IsChecked = true };
        motionMenu.Items.Add(automatic);
        var motionItems = new List<MenuItem> { automatic };
        automatic.Click += (_, _) => SelectMotion(automatic, null);
        foreach (var action in Enum.GetValues<RabbitAction>().Where(a => a != RabbitAction.Drag))
        {
            var item = new MenuItem { Header = RabbitMotion.Label(action), IsCheckable = true };
            item.Click += (_, _) => SelectMotion(item, action);
            motionItems.Add(item);
            motionMenu.Items.Add(item);
        }
        void SelectMotion(MenuItem selected, RabbitAction? action)
        {
            foreach (var item in motionItems) item.IsChecked = item == selected;
            animator.Select(action);
        }
        Add("샘플 보기 / 실제 연결", async () => { demo = !demo; lastUpdated = null; await RefreshAsync(); });
        Add("항상 위에 표시 켜기 / 끄기", () => Topmost = !Topmost);
        Add("종료", Close);
        ContextMenu = menu;
        void Add(string label, Action click)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        Loaded += async (_, _) =>
        {
            animator.Start();
            await RefreshAsync();
            timer.Start();
        };
        timer.Tick += async (_, _) => await RefreshAsync();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) animator.Pause(); else animator.Start(); };
        Closing += (_, _) =>
        {
            timer.Stop();
            animator.Pause();
            closing.Cancel();
            SaveSettings();
        };
        if (demo) ShowDemo();
        else ShowPlaceholder("—", "연결 준비 중", "Codex의 실제 한도를 확인합니다.");
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var temporary = settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { codexPath, left = Left, top = Top, view = view.ToString() }));
            File.Move(temporary, settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void SetView(PetView mode, bool preserveFeet = true)
    {
        var feet = Top + Height - (view == PetView.Details ? 40 : 0);
        var center = Left + Width / 2;
        view = mode;
        var detailed = mode == PetView.Details;
        Width = detailed ? 336 : 300;
        Height = detailed ? 660 : mode == PetView.Summary ? 300 : 230;
        Card.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;
        MotionLabel.Visibility = PetCaption.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;
        SummaryBubble.Visibility = mode == PetView.Summary ? Visibility.Visible : Visibility.Collapsed;
        Pet.Margin = new Thickness(0, 0, 0, detailed ? 40 : 0);
        if (preserveFeet) { Left = center - Width / 2; Top = feet + (detailed ? 40 : 0) - Height; }
        var work = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    async Task RefreshAsync()
    {
        if (busy || closing.IsCancellationRequested) return;
        if (demo) { ShowDemo(); return; }
        if (codexPath == null)
        {
            ShowPlaceholder("—", "연동 필요", "연동 설정에서 codex.exe를 선택해 주세요. Codex CLI의 ChatGPT 로그인이 필요해요.");
            return;
        }
        busy = true;
        RefreshButton.IsEnabled = false;
        Badge.Text = "확인 중";
        SummaryTitle.Text = "Codex";
        SummaryValue.Text = "사용량 확인 중…";
        try
        {
            var quotas = await Usage.ReadAsync(codexPath, closing.Token);
            if (closing.IsCancellationRequested) return;
            lastUpdated = DateTimeOffset.Now;
            Badge.Text = "실시간 연결";
            if (quotas.Count == 0)
                ShowPlaceholder("—", "정보 없음", "계정에서 조회 가능한 한도를 제공하지 않았어요. 잔여량은 알 수 없습니다.");
            else
            {
                Render(quotas);
                Status.Text = $"{lastUpdated:HH:mm} 확인 · 5분마다 갱신\n잔여 토큰 수 대신 제공된 한도를 표시해요.";
                SummaryBubble.ToolTip = Status.Text + "\n우클릭 → 표시 모드 → 상세 카드 + 펫";
            }
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception or JsonException)
        {
            if (closing.IsCancellationRequested) return;
            ShowPlaceholder("?", "연결 실패", (lastUpdated.HasValue ? $"마지막 성공 {lastUpdated:HH:mm}. " : "") + ex.Message);
        }
        finally { busy = false; RefreshButton.IsEnabled = true; }
    }

    void ShowDemo()
    {
        Badge.Text = "샘플 데이터";
        Render(new List<Quota> {
            new("Codex", "5시간", 77, DateTimeOffset.Now.AddHours(2)),
            new("Codex", "7일", 43, DateTimeOffset.Now.AddDays(3))
        });
        Status.Text = "미리보기예요 · 실제 계정 사용량이 아닙니다.\n연동 설정에서 실제 연결로 전환하세요.";
        SummaryBubble.ToolTip = Status.Text;
    }

    void ShowPlaceholder(string value, string badge, string message)
    {
        Badge.Text = badge;
        QuotaRows.Children.Clear();
        QuotaRows.Children.Add(new TextBlock { Text = value, FontSize = 38, Foreground = Brush("#A1B6AA") });
        Status.Text = message;
        PetCaption.Text = "토끼와 함께 연결을 기다려요";
        animator.SetQuota(null);
        SummaryTitle.Text = "Codex";
        SummaryValue.Text = badge;
        SummaryValue.Foreground = Brush("#87745C");
        SummaryBubble.ToolTip = message + "\n우클릭 → 연동 설정";
    }

    void Render(List<Quota> quotas)
    {
        QuotaRows.Children.Clear();
        foreach (var quota in quotas)
        {
            var color = quota.Remaining <= 10 ? "#F0A38D" : quota.Remaining <= 25 ? "#E6CA84" : "#B9E6A4";
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var header = new Grid();
            header.Children.Add(new TextBlock { Text = $"{quota.Bucket} · {quota.Window}", FontSize = 11, Foreground = Brush("#BCCBC2"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 192, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis });
            header.Children.Add(new TextBlock { Text = $"{quota.Remaining:0.#}%", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Brush(color), HorizontalAlignment = HorizontalAlignment.Right });
            row.Children.Add(header);
            var track = new Grid { Height = 5, Margin = new Thickness(0, 7, 0, 7) };
            track.Children.Add(new Border { Background = Brush("#334239"), CornerRadius = new CornerRadius(3) });
            var fill = new Border { Background = Brush(color), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * quota.Remaining / 100;
            track.Children.Add(fill);
            row.Children.Add(track);
            row.Children.Add(new TextBlock { Text = quota.ResetsAt.HasValue ? $"{quota.ResetsAt.Value.ToLocalTime():MM.dd HH:mm} 초기화 (현지 시각)" : "초기화 시각 미제공", FontSize = 10, Foreground = Brush("#849B8C") });
            QuotaRows.Children.Add(row);
        }
        var remaining = quotas.Min(q => q.Remaining);
        SummaryTitle.Text = $"{(quotas.Select(q => q.Bucket).Distinct().Count() == 1 ? quotas[0].Bucket : "AI")} · {(demo ? "샘플 데이터" : "남은 한도")}";
        SummaryValue.Text = Usage.Summarize(quotas);
        SummaryValue.Foreground = Brush(remaining <= 10 ? "#A34534" : "#454531");
        PetCaption.Text = remaining <= 10 ? "잠깐 쉬어가도 괜찮아요" : remaining <= 25 ? "한도가 얼마 남지 않았어요" : "아직 여유 있어요, 함께해요";
        animator.SetQuota(remaining);
    }

    static SolidColorBrush Brush(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        if (Pet.IsMouseOver && e.ClickCount == 2) { animator.Greet(); e.Handled = true; return; }
        var petDrag = Pet.IsMouseOver;
        if (petDrag) animator.BeginDrag();
        try { DragMove(); }
        finally { if (petDrag) animator.EndDrag(); }
    }

    public void PreviewMotion(RabbitAction action, double seconds)
    {
        animator.Preview(action, seconds);
        MotionLabel.Text = RabbitMotion.Label(action) + " · 미리보기";
    }

    public static void SelfTest()
    {
        var window = new PetWindow(true, PetView.Summary, loadSettings: false);
        if (window.Card.Visibility != Visibility.Collapsed || window.SummaryBubble.Visibility != Visibility.Visible ||
            !window.SummaryTitle.Text.Contains("샘플") || window.SummaryValue.Text != "5시간 77% · 주간 43%")
            throw new Exception("Compact mode must show only the pet and an honest quota summary.");
        window.PreviewMotion(RabbitAction.Walk, 1);
        var walk = RabbitMotion.Sample(RabbitAction.Walk, 1);
        if (window.BubbleMove.X != walk.X || window.BubbleMove.Y != walk.Y)
            throw new Exception("The summary must follow the pet without mirroring text.");
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
        foreach (var action in Enum.GetValues<RabbitAction>())
        {
            for (var frame = 0; frame < 180; frame++)
            {
                window.PreviewMotion(action, frame / 30.0);
                var bounds = window.SummaryBubble.TransformToAncestor(content).TransformBounds(new Rect(window.SummaryBubble.RenderSize));
                if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > content.ActualWidth || bounds.Bottom > content.ActualHeight)
                    throw new Exception($"The head summary must stay inside the window during {action}: {bounds}.");
            }
        }
        window.SetView(PetView.PetOnly);
        if (window.Card.Visibility != Visibility.Collapsed || window.SummaryBubble.Visibility != Visibility.Collapsed ||
            window.MotionLabel.Visibility != Visibility.Collapsed || window.PetCaption.Visibility != Visibility.Collapsed || window.Height != 230)
            throw new Exception("Pet-only mode must hide all extra UI.");
        window.SetView(PetView.Details);
        if (window.Card.Visibility != Visibility.Visible || window.SummaryBubble.Visibility != Visibility.Collapsed || window.Height != 660)
            throw new Exception("Detailed mode must restore the full card.");
        window.ShowPlaceholder("?", "연결 실패", "Test connection failure");
        if (window.SummaryValue.Text.Contains('%') || window.SummaryValue.Text != "연결 실패")
            throw new Exception("A failed connection must not show old or invented quota.");
        window.demo = false;
        window.Render(new List<Quota> { new("Codex", "7일", 0, null) });
        if (!window.SummaryTitle.Text.Contains("남은 한도") || window.SummaryValue.Text != "주간 0%")
            throw new Exception("A real exhausted quota must display zero correctly.");
    }
    async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Exit(object sender, RoutedEventArgs e) => Close();
    void SettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    void ShowSettings()
    {
        if (busy) return;
        var dialog = new Window { Title = "TokenPet · 연동 설정", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#18231E"), Foreground = Brush("#E4EDE5"), FontFamily = FontFamily };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Codex 연결", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "ChatGPT로 로그인한 Codex CLI를 사용합니다.\n터미널에서 codex login 후 실행 파일을 선택하세요.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        var pathLabel = new TextBlock { Text = codexPath ?? "codex.exe를 찾지 못했어요", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#A2B6A6") };
        panel.Children.Add(pathLabel);
        var choose = new Button { Content = "codex.exe 선택…", Margin = new Thickness(0, 12, 0, 8), Padding = new Thickness(8), Foreground = Brush("#18231E"), Background = Brush("#C7DFBB") };
        choose.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Codex 실행 파일 (codex.exe)|codex.exe", Title = "설치된 Codex 실행 파일 선택" };
            if (picker.ShowDialog(dialog) == true) { codexPath = picker.FileName; pathLabel.Text = codexPath; SaveSettings(); }
        };
        panel.Children.Add(choose);
        var connect = new Button { Content = "실제 사용량 연결", Padding = new Thickness(8), Foreground = Brush("#18231E"), Background = Brush("#C7DFBB") };
        connect.Click += async (_, _) => { demo = false; lastUpdated = null; dialog.Close(); await RefreshAsync(); };
        panel.Children.Add(connect);
        dialog.Content = panel;
        dialog.ShowDialog();
    }
}
