using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BandoriLove;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<VoiceCard> cards = [];
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
        UpdateControls();
    }

    private void AddCard(object sender, RoutedEventArgs e) => OpenCard(null);
    private void EditCard(object sender, RoutedEventArgs e) => OpenCard(Cards.SelectedItem as VoiceCard);

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
        TextCount.Text = $"{ScriptText.Text.Length} / 3000";
        EditButton.IsEnabled = card is not null;
        GenerateButton.IsEnabled = generation is null && card is not null && !string.IsNullOrWhiteSpace(ScriptText.Text);
        CancelButton.Visibility = generation is not null ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.IsEnabled = SaveButton.IsEnabled = StopButton.IsEnabled = audio is not null;
    }

    private async void Generate(object sender, RoutedEventArgs e)
    {
        if (generation is not null || Cards.SelectedItem is not VoiceCard card
            || InputLanguage.SelectedValue is not string language || string.IsNullOrWhiteSpace(ScriptText.Text)) return;
        using var request = new CancellationTokenSource();
        generation = request;
        var text = ScriptText.Text.Trim();
        Status.Text = "正在等待 GPT-SoVITS 返回音频…";
        UpdateControls();
        try
        {
            var result = await GsvClient.SynthesizeAsync(card, text, language, request.Token);
            if (request.IsCancellationRequested) return;
            StopPlayback();
            audio = result;
            audioName = string.Concat(card.Name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrEmpty(audioName)) audioName = "BandoriLove";
            AudioLabel.Text = $"{card.Name} · WAV";
            Status.Text = "语音已生成，可以播放或保存。";
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
        StopPlayback();
        base.OnClosed(e);
    }
}
