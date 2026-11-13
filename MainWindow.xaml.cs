using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BandoriLove;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<VoiceCard> cards = [];
    private readonly LocalEngine localEngine = new();
    private readonly HardwareMonitor hardware = new();
    private readonly CancellationTokenSource monitoring = new();
    private DeviceChoice[] devices = [];
    private string preferredDevice = CardStore.LoadDevice();
    private bool refreshingDevices;
    private bool hardwareReady;
    private IReadOnlyList<GpuReading> gpus = [];
    private CancellationTokenSource? generation;
    private byte[]? audio;
    private SoundPlayer? player;
    private MemoryStream? playbackStream;
    private string audioName = "BandoriLove";

    public MainWindow()
    {
        InitializeComponent();
        Cards.ItemsSource = cards;
        try
        {
            foreach (var card in CardStore.Load()) cards.Add(card);
        }
        catch (Exception ex) { Status.Text = "无法读取角色卡：" + ex.Message; }
        Cards.SelectedIndex = cards.Count > 0 ? 0 : -1;
        Loaded += (_, _) => _ = MonitorHardwareAsync();
        UpdateControls();
    }

    private void ToggleHardware(object sender, RoutedEventArgs e) =>
        HardwarePanel.Visibility = HardwarePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingDevices || DevicePicker.SelectedValue is not string value) return;
        preferredDevice = value;
        try { CardStore.SaveDevice(value); }
        catch (IOException ex) { Status.Text = "无法保存推理设备设置：" + ex.Message; }
        catch (UnauthorizedAccessException ex) { Status.Text = "无法保存推理设备设置：" + ex.Message; }
    }

    private async Task MonitorHardwareAsync()
    {
        var token = monitoring.Token;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                var sample = await hardware.SampleAsync(token);
                if (token.IsCancellationRequested) return;
                gpus = sample.Gpus;
                if (!hardwareReady)
                {
                    hardwareReady = true;
                    UpdateControls();
                }
                SystemReading.Text = $"CPU  {Percent(sample.Cpu)}     内存  {Percent(sample.Ram)}";
                GpuReading.Text = sample.Gpus.Count == 0 ? "未检测到 NVIDIA CUDA 显卡"
                    : string.Join("\n", sample.Gpus.Select(gpu => $"{gpu.Name}   {gpu.Usage}"));
                var choices = new List<DeviceChoice> { new("auto", "自动 · 优先显卡") };
                if (HardwareMonitor.CudaInstalled)
                    choices.AddRange(sample.Gpus.Select(gpu => new DeviceChoice(gpu.Id, gpu.Name)));
                else if (sample.Gpus.Count > 0)
                    GpuReading.Text += "\n未安装 CUDA 推理运行库";
                if (preferredDevice.StartsWith("GPU-", StringComparison.Ordinal)
                    && !choices.Any(choice => choice.Id == preferredDevice))
                    choices.Add(new(preferredDevice, "上次选择的显卡不可用"));
                choices.Add(new("cpu", "CPU"));
                if (devices.SequenceEqual(choices)) continue;
                refreshingDevices = true;
                devices = choices.ToArray();
                DevicePicker.ItemsSource = devices;
                DevicePicker.SelectedValue = devices.Any(device => device.Id == preferredDevice) ? preferredDevice : "auto";
                refreshingDevices = false;
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static string Percent(int? value) => value is int number ? $"{number}%" : "—";

    private void AddCard(object sender, RoutedEventArgs e) => OpenCard(null);
    private void EditCard(object sender, RoutedEventArgs e) => OpenCard(Cards.SelectedItem as VoiceCard);

    private void ImportCards(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "角色卡 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = CardStore.Read(dialog.FileName);
            if (imported.Count == 0) throw new InvalidDataException("文件中没有角色卡。");
            foreach (var card in imported) GsvClient.ValidateCard(card);
            var selected = (Cards.SelectedItem as VoiceCard)?.Id ?? imported[0].Id;
            var merged = cards.Concat(imported).GroupBy(card => card.Id).Select(group => group.Last()).ToList();
            CardStore.Save(merged);
            cards.Clear();
            foreach (var card in merged) cards.Add(card);
            Cards.SelectedItem = cards.First(card => card.Id == selected);
            Status.Text = $"已导入 {imported.Count} 张角色卡。";
        }
        catch (Exception ex) { Status.Text = "角色卡导入失败：" + ex.Message; }
        UpdateControls();
    }

    private void OpenCard(VoiceCard? existing)
    {
        var dialog = new CardWindow(existing) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Card is not { } card) return;
        var next = cards.ToList();
        var index = existing is null ? -1 : next.IndexOf(existing);
        if (index < 0) next.Add(card);
        else next[index] = card;
        try
        {
            CardStore.Save(next);
            if (index < 0) cards.Add(card);
            else cards[index] = card;
            Cards.SelectedItem = card;
            Status.Text = "角色卡已保存。";
        }
        catch (Exception ex) { Status.Text = "角色卡保存失败：" + ex.Message; }
        UpdateControls();
    }

    private void CardChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();
    private void ScriptChanged(object sender, TextChangedEventArgs e) => UpdateControls();

    private void UpdateControls()
    {
        if (GenerateButton is null) return;
        var card = Cards.SelectedItem as VoiceCard;
        EmptyCards.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Cards.Visibility = cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CharacterLabel.Text = card?.Name ?? "请先添加角色卡";
        TextCount.Text = $"{ScriptText.Text.Length} / 1000";
        EditButton.IsEnabled = card is { Local: false };
        GenerateButton.IsEnabled = generation is null && card is not null && (hardwareReady || !card.Local)
            && !string.IsNullOrWhiteSpace(ScriptText.Text);
        CancelButton.Visibility = generation is not null ? Visibility.Visible : Visibility.Collapsed;
        DevicePicker.IsEnabled = generation is null;
        PlayButton.IsEnabled = SaveButton.IsEnabled = StopButton.IsEnabled = audio is not null;
    }

    private async void Generate(object sender, RoutedEventArgs e)
    {
        if (generation is not null || Cards.SelectedItem is not VoiceCard card
            || InputLanguage.SelectedValue is not string language || string.IsNullOrWhiteSpace(ScriptText.Text)) return;
        using var request = new CancellationTokenSource();
        generation = request;
        var text = ScriptText.Text.Trim();
        UpdateControls();
        try
        {
            byte[] result;
            string used;
            if (card.Local)
            {
                var selection = preferredDevice;
                var gpu = HardwareMonitor.CudaInstalled ? gpus
                    .Where(item => selection == "auto" || item.Id == selection)
                    .OrderByDescending(item => item.TotalMiB - (item.UsedMiB ?? 0)).FirstOrDefault() : null;
                if (selection != "auto" && selection != "cpu" && gpu is null)
                    throw new InvalidOperationException("之前选择的显卡不可用，请在「硬件」中重新选择。");
                used = gpu?.Name ?? "CPU";
                Status.Text = $"正在使用 {used} 合成，首次需要载入模型…";
                try { result = await localEngine.SynthesizeAsync(card, text, language, gpu?.Id ?? "cpu", request.Token); }
                catch (EngineStartupException) when (gpu is not null && selection == "auto")
                {
                    used = "CPU";
                    Status.Text = "显卡初始化失败，已改用 CPU 合成…";
                    result = await localEngine.SynthesizeAsync(card, text, language, "cpu", request.Token);
                }
            }
            else
            {
                used = "远程服务";
                Status.Text = "正在等待 GPT-SoVITS 返回音频…";
                result = await GsvClient.SynthesizeAsync(card, text, language, request.Token);
            }
            if (request.IsCancellationRequested) return;
            StopPlayback();
            audio = result;
            audioName = string.Concat(card.Name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrEmpty(audioName)) audioName = "BandoriLove";
            AudioLabel.Text = $"{card.Name} · WAV";
            Status.Text = $"语音已生成 · {used}";
        }
        catch (OperationCanceledException)
        {
            Status.Text = request.IsCancellationRequested ? "已取消等待。" : "生成超时，请检查推理服务。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            generation = null;
            UpdateControls();
        }
    }

    private void CancelGeneration(object sender, RoutedEventArgs e) => generation?.Cancel();

    private void PlayAudio(object sender, RoutedEventArgs e)
    {
        if (audio is null) return;
        StopPlayback();
        try
        {
            playbackStream = new MemoryStream(audio, writable: false);
            player = new SoundPlayer(playbackStream);
            player.Play();
        }
        catch (Exception ex)
        {
            StopPlayback();
            Status.Text = "播放失败：" + ex.Message;
        }
    }

    private void StopAudio(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        player?.Stop();
        player?.Dispose();
        playbackStream?.Dispose();
        player = null;
        playbackStream = null;
    }

    private async void SaveAudio(object sender, RoutedEventArgs e)
    {
        var current = audio;
        if (current is null) return;
        var dialog = new SaveFileDialog
        {
            FileName = $"{audioName}-{DateTime.Now:yyyyMMdd-HHmmss}.wav",
            DefaultExt = ".wav",
            Filter = "WAV 音频 (*.wav)|*.wav"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, current);
            Status.Text = "已保存 " + dialog.FileName;
        }
        catch (Exception ex) { Status.Text = "保存失败：" + ex.Message; }
    }

    protected override void OnClosed(EventArgs e)
    {
        generation?.Cancel();
        monitoring.Cancel();
        monitoring.Dispose();
        localEngine.Dispose();
        StopPlayback();
        base.OnClosed(e);
    }
}
