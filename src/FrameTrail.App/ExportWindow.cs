using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FrameTrail;
using FrameTrail.Media;

namespace FrameTrail.App;

/// <summary>DogSC 风格的独立导出面板。执行和进度均由调用方提供的导出引擎负责。</summary>
public sealed partial class ExportWindow : Window
{
    private readonly FrameTrailProject project;
    private readonly ImageSource? preview;
    private readonly Func<string, ExportOptions, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> execute;
    private readonly TextBox fileName = new();
    private readonly TextBlock extensionBadge = new();
    private readonly TextBlock directoryText = new();
    private readonly TextBlock previewFormat = new();
    private readonly TextBlock previewSize = new();
    private readonly TextBlock previewRate = new();
    private readonly TextBlock previewDuration = new();
    private readonly TextBlock stageText = new();
    private readonly TextBlock timeText = new();
    private readonly TextBlock estimate = new();
    private readonly ProgressBar progressBar = new();
    private readonly List<Button> resolutionChoices = new();
    private readonly List<Button> frameRateChoices = new();
    private readonly CheckBox hardware = new();
    private readonly Button exportButton = new();
    private readonly Button videoModeButton = new();
    private readonly Button audioModeButton = new();
    private readonly Button cancelButton = new();
    private readonly Button openFileButton = new();
    private readonly Button openFolderButton = new();
    private readonly Button closeButton = new();
    private readonly Button browseButton = new();
    private RowDefinition? progressSpacerRow;
    private bool audioOnly;
    private bool running;
    private int resolutionIndex = 1;
    private int frameRateIndex;
    private string outputDirectory;
    private string? completedPath;
    private CancellationTokenSource? cancellation;

    public ExportWindow(FrameTrailProject project, ImageSource? preview, string projectName,
        Func<string, ExportOptions, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> execute,
        bool initialAudioOnly = false)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.preview = preview;
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FrameTrail");
        audioOnly = initialAudioOnly;
        Title = "导出作品 · " + projectName;
        Width = 840;
        Height = 634;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        MinWidth = 840;
        MinHeight = 634;
        MaxWidth = 840;
        MaxHeight = 634;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        fileName.Text = SafeStem(projectName);

        Content = BuildLayout(projectName);
        RefreshPreview();
        UpdateMode();
        Closed += (_, _) => cancellation?.Cancel();
    }

    private UIElement BuildLayout(string projectName)
    {
        var root = new Grid { Background = Brush("Workspace") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });

        var header = new Grid { Background = Brush("Chrome"), Margin = new Thickness(0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var icon = new EditorIcon { Kind = "export", Width = 19, Height = 19, Stroke = Brush("Ink"), Opacity = .72, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(-2.5, 0, 0, 0) };
        header.Children.Add(icon);
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock { Text = "导出作品", FontSize = 20, FontWeight = FontWeights.Medium });
        titleStack.Children.Add(new TextBlock { Text = projectName, FontSize = 12, Foreground = Brush("Secondary"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(titleStack, 1); header.Children.Add(titleStack);
        closeButton.Width = 36; closeButton.Height = 36; closeButton.Style = FindStyle("PlainButton"); closeButton.Padding = new Thickness(0);
        closeButton.Content = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brush("ControlSurface"), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(.75), Child = new EditorIcon { Kind = "close", Width = 12, Height = 12, Stroke = Brush("Secondary"), Opacity = .68, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        closeButton.Click += (_, _) => Close(); System.Windows.Automation.AutomationProperties.SetName(closeButton, "关闭导出窗口"); Grid.SetColumn(closeButton, 2); header.Children.Add(closeButton);
        Border headerBorder = new() { Child = header, Background = Brush("Chrome"), Padding = new Thickness(22, 0, 18, 0), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == System.Windows.Input.MouseButton.Left && e.OriginalSource is not Button) DragMove(); };
        Grid.SetRow(headerBorder, 0); root.Children.Add(headerBorder);

        var body = new Grid { Background = Brush("Surface") };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(432) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(408) });
        var left = new StackPanel { Margin = new Thickness(30, 9, 29, 5) };
        left.Children.Add(Label("文件名"));
        var nameRow = new Grid { Height = 44 };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition()); nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileName.Margin = new Thickness(0); fileName.Padding = new Thickness(13, 1, 4, 1); fileName.Height = 44; fileName.MinHeight = 0; fileName.VerticalContentAlignment = VerticalAlignment.Center; fileName.Background = Brushes.Transparent; fileName.FontSize = 13; fileName.TextChanged += (_, _) => RefreshPreview();
        System.Windows.Automation.AutomationProperties.SetName(fileName, "导出文件名");
        nameRow.Children.Add(fileName);
        var nameShell = new Border { Background = Brush("Chrome"), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(.75), CornerRadius = new CornerRadius(12), Padding = new Thickness(0, 0, 5, 0) };
        nameShell.Child = nameRow;
        var badge = new Border { Background = Brush("ControlSurface"), CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.Child = extensionBadge; Grid.SetColumn(badge, 1); nameRow.Children.Add(badge);
        var editName = new Button { Style = FindStyle("PlainButton"), Padding = new Thickness(4), Margin = new Thickness(3, 0, 2, 0), ToolTip = "编辑文件名" };
        editName.Content = new EditorIcon { Kind = "pencil", Width = 14, Height = 14, Stroke = Brush("Secondary") }; editName.Click += (_, _) => { fileName.Focus(); fileName.SelectAll(); };
        System.Windows.Automation.AutomationProperties.SetName(editName, "编辑文件名"); Grid.SetColumn(editName, 2); nameRow.Children.Add(editName);
        left.Children.Add(nameShell);

        var dirRow = new Grid { Height = 36, Margin = new Thickness(0, 8, 0, 0) };
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); dirRow.ColumnDefinitions.Add(new ColumnDefinition()); dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folderIcon = new EditorIcon { Kind = "folder", Width = 16, Height = 16, Stroke = Brush("Secondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 9, 0) }; dirRow.Children.Add(folderIcon);
        directoryText.TextTrimming = TextTrimming.CharacterEllipsis; directoryText.VerticalAlignment = VerticalAlignment.Center; directoryText.Foreground = Brush("Secondary"); System.Windows.Automation.AutomationProperties.SetName(directoryText, "保存到"); Grid.SetColumn(directoryText, 1); dirRow.Children.Add(directoryText);
        browseButton.Content = new EditorIcon { Kind = "chevron", Width = 13, Height = 13, Stroke = Brush("Secondary") }; browseButton.Style = FindStyle("PlainButton"); browseButton.Padding = new Thickness(8); browseButton.ToolTip = "选择保存位置";
        System.Windows.Automation.AutomationProperties.SetName(browseButton, "选择保存位置"); browseButton.Click += (_, _) => ChooseOutputPath(); Grid.SetColumn(browseButton, 2); dirRow.Children.Add(browseButton); left.Children.Add(dirRow);

        var contentLabel = Label("导出内容"); contentLabel.Foreground = Brush("Ink"); contentLabel.FontSize = 13; contentLabel.Margin = new Thickness(0, 17, 0, 16); left.Children.Add(contentLabel);
        var modes = new UniformGrid { Columns = 2, Height = 40 };
        videoModeButton.Content = ModeOptionContent("视频成片"); System.Windows.Automation.AutomationProperties.SetName(videoModeButton, "视频成片"); videoModeButton.Style = FindStyle("PlainButton"); videoModeButton.Height = 36; videoModeButton.Margin = new Thickness(2); videoModeButton.Click += (_, _) => { audioOnly = false; UpdateMode(); RefreshPreview(); };
        audioModeButton.Content = ModeOptionContent("仅导出音频"); System.Windows.Automation.AutomationProperties.SetName(audioModeButton, "仅导出音频"); audioModeButton.Style = FindStyle("PlainButton"); audioModeButton.Height = 36; audioModeButton.Margin = new Thickness(2); audioModeButton.Click += (_, _) => { audioOnly = true; UpdateMode(); RefreshPreview(); };
        modes.Children.Add(videoModeButton); modes.Children.Add(audioModeButton); left.Children.Add(modes);

        var resolutionLabel = Label("分辨率"); resolutionLabel.Foreground = Brush("Ink"); resolutionLabel.FontSize = 13; resolutionLabel.Margin = new Thickness(0, 17, 0, 16); left.Children.Add(resolutionLabel);
        var resolutions = new UniformGrid { Columns = 4, Height = 40 };
        AddChoice(resolutions, resolutionChoices, new[] { "原始素材", "1080p", "1440p", "4K" }, index => { resolutionIndex = index; UpdateChoiceStates(); RefreshPreview(); }, "ExportResolution");
        left.Children.Add(resolutions);
        var frameRateLabel = Label("帧率"); frameRateLabel.Foreground = Brush("Ink"); frameRateLabel.FontSize = 13; frameRateLabel.Margin = new Thickness(0, 17, 0, 16); left.Children.Add(frameRateLabel);
        var rates = new UniformGrid { Columns = 2, Height = 40 };
        AddChoice(rates, frameRateChoices, new[] { "30 FPS", "60 FPS" }, index => { frameRateIndex = index; UpdateChoiceStates(); RefreshPreview(); }, "ExportFrameRate");
        left.Children.Add(rates);
        hardware.Content = "硬件编码"; hardware.IsChecked = true; hardware.Margin = new Thickness(0, 8, 0, 0); hardware.FontSize = 11; System.Windows.Automation.AutomationProperties.SetName(hardware, "优先使用硬件编码"); hardware.Checked += (_, _) => RefreshPreview(); hardware.Unchecked += (_, _) => RefreshPreview(); left.Children.Add(hardware);
        Grid.SetColumn(left, 0); body.Children.Add(left);

        var rightBorder = new Border { BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(0, 29, 0, 28), Padding = new Thickness(28, 0, 29, 0) };
        var right = new Grid(); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) }); right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(203) }); right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var previewTitle = new TextBlock { Text = "当前画面", FontSize = 13, Foreground = Brush("Secondary"), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewTitle, "ExportPreviewTitle"); Grid.SetRow(previewTitle, 0); right.Children.Add(previewTitle);
        var imageFrame = new Border { Background = DottedPreviewSurface(), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(.75), CornerRadius = new CornerRadius(16), ClipToBounds = true, Margin = new Thickness(0, 11, 0, 0) };
        if (preview != null)
        {
            imageFrame.Child = new Image { Source = preview, Stretch = Stretch.Uniform, Margin = new Thickness(18, 8, 18, 8) };
        }
        else imageFrame.Child = new TextBlock { Text = "当前画面预览", Foreground = Brush("Secondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(imageFrame, "ExportPreviewImage"); Grid.SetRow(imageFrame, 1); right.Children.Add(imageFrame);
        var info = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewFormat, "ExportSummaryFormat");
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewSize, "ExportSummaryResolution");
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewRate, "ExportSummaryFrameRate");
        System.Windows.Automation.AutomationProperties.SetAutomationId(previewDuration, "ExportSummaryDuration");
        info.Children.Add(InfoLine("格式", previewFormat)); info.Children.Add(InfoLine("分辨率", previewSize)); info.Children.Add(InfoLine("帧率", previewRate)); info.Children.Add(InfoLine("时长", previewDuration));
        Grid.SetRow(info, 2); right.Children.Add(info);
        Grid.SetColumn(rightBorder, 1); rightBorder.Child = right; body.Children.Add(rightBorder);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var footer = new Grid { Background = Brush("Chrome"), Margin = new Thickness(0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(17) }); progressSpacerRow = new RowDefinition { Height = new GridLength(0) }; footer.RowDefinitions.Add(progressSpacerRow); footer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var progressPanel = new Grid { Margin = new Thickness(30, 1, 30, 0) };
        progressPanel.ColumnDefinitions.Add(new ColumnDefinition()); progressPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stageText.Text = ""; stageText.Foreground = Brush("Secondary"); progressPanel.Children.Add(stageText);
        timeText.Text = ""; timeText.Foreground = Brush("Secondary"); Grid.SetColumn(timeText, 1); progressPanel.Children.Add(timeText);
        Grid.SetRow(progressPanel, 0); footer.Children.Add(progressPanel);
        progressBar.Minimum = 0; progressBar.Maximum = 1; progressBar.Height = 4; progressBar.Margin = new Thickness(30, 0, 30, 0); progressBar.Visibility = Visibility.Collapsed;
        Grid.SetRow(progressBar, 1); Grid.SetColumnSpan(progressBar, 2); footer.Children.Add(progressBar);
        var actions = new Grid { Height = 42, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(27, 1, 27, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        estimate.Foreground = Brush("Secondary"); estimate.VerticalAlignment = VerticalAlignment.Center; estimate.FontSize = 11; estimate.TextWrapping = TextWrapping.Wrap;
        actions.Children.Add(estimate);
        cancelButton.Content = "取消"; cancelButton.Style = FindStyle("PlainButton"); cancelButton.Padding = new Thickness(0); cancelButton.MinWidth = 58; cancelButton.Height = 40; cancelButton.Click += (_, _) => CancelExport(); Grid.SetColumn(cancelButton, 1); actions.Children.Add(cancelButton);
        openFolderButton.Content = "打开文件夹"; openFolderButton.Visibility = Visibility.Collapsed; openFolderButton.Height = 40; openFolderButton.Click += (_, _) => OpenCompleted(false); Grid.SetColumn(openFolderButton, 2); actions.Children.Add(openFolderButton);
        openFileButton.Content = "打开文件"; openFileButton.Visibility = Visibility.Collapsed; openFileButton.Style = FindStyle("PrimaryButton"); openFileButton.MinWidth = 130; openFileButton.Height = 42; openFileButton.Click += (_, _) => OpenCompleted(true); Grid.SetColumn(openFileButton, 3); actions.Children.Add(openFileButton);
        exportButton.Content = ExportButtonContent("开始导出视频", true); System.Windows.Automation.AutomationProperties.SetName(exportButton, "开始导出视频"); exportButton.Style = FindStyle("PrimaryButton"); exportButton.MinWidth = 145; exportButton.Height = 42; exportButton.Margin = new Thickness(7, 0, 0, 0); exportButton.Click += async (_, _) => await StartExport(); Grid.SetColumn(exportButton, 3); actions.Children.Add(exportButton);
        Grid.SetRow(actions, 2); footer.Children.Add(actions);
        Border footerBorder = new() { Child = footer, BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(footerBorder, 2); root.Children.Add(footerBorder);
        var shell = new Border { Background = Brush("Surface"), CornerRadius = new CornerRadius(18), Child = root };
        shell.SizeChanged += (_, _) => shell.Clip = new RectangleGeometry(new Rect(0, 0, shell.ActualWidth, shell.ActualHeight), 18, 18);
        return shell;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Brush("Secondary"), FontSize = 12, Margin = new Thickness(0, 17, 0, 9) };
    private static Grid ModeOptionContent(string text)
    {
        var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var check = new TextBlock { Text = "✓", FontSize = 9, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 7, 0) };
        Grid.SetColumn(check, 1); content.Children.Add(check); return content;
    }
    private static DrawingBrush DottedPreviewSurface()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(248, 249, 249)), null, new RectangleGeometry(new Rect(0, 0, 14, 14))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(218, 222, 223)), null, new EllipseGeometry(new Point(7, 7), 1, 1)));
        return new DrawingBrush(drawing) { Stretch = Stretch.Fill, TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, 14, 14), ViewboxUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 14, 14), ViewportUnits = BrushMappingMode.Absolute };
    }
    private static UIElement InfoLine(string label, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Brush("Secondary"), FontSize = 12 });
        value.FontSize = 12; Grid.SetColumn(value, 1); grid.Children.Add(value); return grid;
    }
    private void AddChoice(UniformGrid host, List<Button> choices, string[] labels, Action<int> select, string automationPrefix)
    {
        for (var index = 0; index < labels.Length; index++)
        {
            var selectedIndex = index;
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = labels[index], VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            var check = new TextBlock { Text = "✓", FontSize = 9, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, -3, 0) };
            Grid.SetColumn(check, 1); content.Children.Add(check);
            var button = new Button { Content = content, Height = 36, Margin = new Thickness(2), Padding = new Thickness(3, 5, 3, 5), FontSize = 13, BorderThickness = new Thickness(.75), BorderBrush = Brush("Hairline") };
            System.Windows.Automation.AutomationProperties.SetName(button, labels[index]);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationPrefix + "_" + index);
            button.Click += (_, _) => select(selectedIndex);
            choices.Add(button); host.Children.Add(button);
        }
        UpdateChoiceStates();
    }

    private void UpdateChoiceStates()
    {
        for (var i = 0; i < resolutionChoices.Count; i++)
        {
            resolutionChoices[i].Background = i == resolutionIndex ? Brush("Chrome") : Brush("ControlSurface");
            resolutionChoices[i].Foreground = i == resolutionIndex ? Brush("Ink") : Brush("Secondary");
            resolutionChoices[i].FontWeight = i == resolutionIndex ? FontWeights.SemiBold : FontWeights.Normal;
            if (resolutionChoices[i].Content is Grid g && g.Children.Count > 1) g.Children[1].Visibility = i == resolutionIndex ? Visibility.Visible : Visibility.Collapsed;
        }
        for (var i = 0; i < frameRateChoices.Count; i++)
        {
            frameRateChoices[i].Background = i == frameRateIndex ? Brush("Chrome") : Brush("ControlSurface");
            frameRateChoices[i].Foreground = i == frameRateIndex ? Brush("Ink") : Brush("Secondary");
            frameRateChoices[i].FontWeight = i == frameRateIndex ? FontWeights.SemiBold : FontWeights.Normal;
            if (frameRateChoices[i].Content is Grid g && g.Children.Count > 1) g.Children[1].Visibility = i == frameRateIndex ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private Button MakeButton(string text) => new() { Content = text, Margin = new Thickness(2), Padding = new Thickness(10, 8, 10, 8), Style = FindStyle("PlainButton"), BorderBrush = Brush("Hairline"), BorderThickness = new Thickness(1) };
    private static Brush Brush(string key) => (Brush)(Application.Current?.TryFindResource(key) ?? Brushes.Gray);
    private Style? FindStyle(string key) => TryFindResource(key) as Style ?? Application.Current?.TryFindResource(key) as Style;

    private void UpdateMode()
    {
        extensionBadge.Text = audioOnly ? ".m4a" : ".mp4";
        var exportLabel = running ? "正在导出…" : audioOnly ? "导出音频" : "开始导出视频";
        exportButton.Content = ExportButtonContent(exportLabel, !running);
        System.Windows.Automation.AutomationProperties.SetName(exportButton, exportLabel);
        exportButton.Visibility = completedPath is null ? Visibility.Visible : Visibility.Collapsed;
        videoModeButton.Background = audioOnly ? Brush("ControlSurface") : Brush("Chrome");
        audioModeButton.Background = audioOnly ? Brush("Chrome") : Brush("ControlSurface");
        videoModeButton.FontWeight = audioOnly ? FontWeights.Normal : FontWeights.Medium;
        audioModeButton.FontWeight = audioOnly ? FontWeights.Medium : FontWeights.Normal;
        if (videoModeButton.Content is Grid videoContent && videoContent.Children.Count > 1) videoContent.Children[1].Visibility = audioOnly ? Visibility.Collapsed : Visibility.Visible;
        if (audioModeButton.Content is Grid audioContent && audioContent.Children.Count > 1) audioContent.Children[1].Visibility = audioOnly ? Visibility.Visible : Visibility.Collapsed;
        foreach (var choice in resolutionChoices) choice.IsEnabled = !audioOnly && !running;
        foreach (var choice in frameRateChoices) choice.IsEnabled = !audioOnly && !running;
        hardware.IsEnabled = !audioOnly && !running;
        videoModeButton.IsEnabled = audioModeButton.IsEnabled = browseButton.IsEnabled = !running;
        fileName.IsEnabled = !running; cancelButton.IsEnabled = true;
    }

    private void RefreshPreview()
    {
        directoryText.Text = outputDirectory;
        var (width, height) = GetSize();
        previewFormat.Text = audioOnly ? "M4A 音频" : "MP4 视频";
        previewSize.Text = audioOnly ? "—" : $"{width} × {height}";
        previewRate.Text = audioOnly ? "—" : (frameRateIndex == 1 ? "60 FPS" : "30 FPS");
        var duration = project.Timeline?.Duration ?? project.Media.Duration;
        previewDuration.Text = double.IsFinite(duration) && duration >= 0 ? TimeSpan.FromSeconds(duration).ToString(duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture) : "—";
        estimate.Text = EstimateFileSize(duration);
    }

    private string EstimateFileSize(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0) return "预计大小：—";
        if (audioOnly)
        {
            var bytes = duration * 192_000 / 8d;
            return $"预计约 {FormatSize(bytes)}（AAC 192 kb/s）";
        }

        var (width, height) = GetSize();
        var frameRate = frameRateIndex == 1 ? 60 : 30;
        var audioBits = 192_000d;
        double lowBitsPerSecond;
        double highBitsPerSecond;
        if (hardware.IsChecked == true)
        {
            // The current hardware path requests 16 Mb/s; actual encoders may vary or fall back.
            lowBitsPerSecond = 12_800_000 + audioBits;
            highBitsPerSecond = 19_200_000 + audioBits;
        }
        else
        {
            // CRF 20 is content-dependent. Use a broad pixel-rate range rather than a point estimate.
            var pixelRate = (double)width * height * frameRate;
            lowBitsPerSecond = pixelRate * .035 + audioBits;
            highBitsPerSecond = pixelRate * .14 + audioBits;
        }

        var lowBytes = duration * lowBitsPerSecond / 8d;
        var highBytes = duration * highBitsPerSecond / 8d;
        return $"预计约 {FormatSize(lowBytes)}–{FormatSize(highBytes)}（含 AAC 192 kb/s，实际大小随画面变化）";
    }

    private static string FormatSize(double bytes)
    {
        const double megabyte = 1_000_000d;
        return $"{bytes / megabyte:0.#} MB";
    }

    private (int Width, int Height) GetSize()
    {
        var index = resolutionIndex;
        var sourceW = Math.Max(16, (int)Math.Round(project.Media.Width > 0 ? project.Media.Width : project.Settings.CanvasWidth));
        var sourceH = Math.Max(16, (int)Math.Round(project.Media.Height > 0 ? project.Media.Height : project.Settings.CanvasHeight));
        if (index == 0) return (Even(sourceW), Even(sourceH));
        var longEdge = index switch { 2 => 2560, 3 => 3840, _ => 1920 };
        var aspect = project.Settings.CanvasWidth > 0 && project.Settings.CanvasHeight > 0 ? (double)project.Settings.CanvasWidth / project.Settings.CanvasHeight : (double)sourceW / sourceH;
        var w = aspect >= 1 ? longEdge : (int)Math.Round(longEdge * aspect);
        var h = aspect >= 1 ? (int)Math.Round(longEdge / aspect) : longEdge;
        return (Even(w), Even(h));
    }
    private static int Even(int value) => Math.Max(16, value / 2 * 2);
    private static string SafeStem(string text)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string((string.IsNullOrWhiteSpace(text) ? "FrameTrail" : text).Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "FrameTrail" : cleaned;
    }

    private void ChooseOutputPath()
    {
        var dialog = new SaveFileDialog { Title = "选择导出文件与保存目录", Filter = audioOnly ? "M4A 音频|*.m4a" : "MP4 视频|*.mp4", DefaultExt = audioOnly ? ".m4a" : ".mp4", AddExtension = true, OverwritePrompt = true, InitialDirectory = Directory.Exists(outputDirectory) ? outputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), FileName = SafeStem(fileName.Text) + (audioOnly ? ".m4a" : ".mp4") };
        if (dialog.ShowDialog(this) != true) return;
        outputDirectory = Path.GetDirectoryName(dialog.FileName) ?? outputDirectory;
        fileName.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        RefreshPreview();
    }

    private async Task StartExport()
    {
        if (running) return;
        var stem = SafeStem(fileName.Text);
        if (string.IsNullOrWhiteSpace(fileName.Text)) { stageText.Text = "请输入文件名。"; return; }
        var outputPath = Path.Combine(outputDirectory, stem + (audioOnly ? ".m4a" : ".mp4"));
        if (File.Exists(outputPath) && MessageBox.Show(this, "目标文件已存在，导出完成后将替换它。是否继续？", "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        running = true; completedPath = null; cancellation = new CancellationTokenSource();
        if (progressSpacerRow is not null) progressSpacerRow.Height = new GridLength(5);
        progressBar.Visibility = Visibility.Visible; progressBar.Value = 0; stageText.Text = "正在准备…"; timeText.Text = "";
        exportButton.IsEnabled = false; openFileButton.Visibility = Visibility.Collapsed; openFolderButton.Visibility = Visibility.Collapsed; UpdateMode();
        var reporter = new Progress<ExportProgress>(p =>
        {
            progressBar.Value = Math.Clamp(p.Fraction, 0, 1); stageText.Text = p.Stage;
            timeText.Text = "已用 " + p.Elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture) + (p.Remaining is { } remaining ? " · 预计剩余 " + remaining.ToString(@"m\:ss", CultureInfo.InvariantCulture) : "");
        });
        try
        {
            var (w, h) = GetSize(); var options = new ExportOptions(w, h, frameRateIndex == 1 ? 60 : 30, hardware.IsChecked == true, audioOnly);
            var result = await execute(outputPath, options, reporter, cancellation.Token);
            completedPath = result.Path; progressBar.Value = 1; stageText.Text = $"导出完成 · {result.Encoder} · {result.Elapsed.TotalSeconds:0.0} 秒";
            timeText.Text = "文件已保存"; openFileButton.Visibility = Visibility.Visible; openFolderButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { stageText.Text = "已取消导出"; timeText.Text = "临时文件已由导出引擎清理"; }
        catch (Exception ex) { stageText.Text = "导出失败，可修改设置后重试"; timeText.Text = ex.Message; }
        finally
        {
            cancellation?.Dispose(); cancellation = null; running = false; exportButton.IsEnabled = true; UpdateMode();
        }
    }

    private void CancelExport()
    {
        if (!running) { Close(); return; }
        cancellation?.Cancel(); cancelButton.IsEnabled = false; stageText.Text = "正在取消…";
    }
    private StackPanel ExportButtonContent(string label, bool showIcon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (showIcon) content.Children.Add(new EditorIcon { Kind = "export", Width = 16, Height = 16, Stroke = Brush("Workspace"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }
    private void OpenCompleted(bool file)
    {
        if (completedPath is not { } path || !File.Exists(path)) { stageText.Text = "找不到已导出的文件。"; return; }
        var target = file ? path : Path.GetDirectoryName(path)!;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
