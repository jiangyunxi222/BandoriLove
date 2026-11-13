using System.Windows;

namespace BandoriLove;

public partial class CardWindow : Window
{
    private readonly VoiceCard original;
    public VoiceCard? Card { get; private set; }

    public CardWindow(VoiceCard? existing)
    {
        InitializeComponent();
        original = existing ?? new VoiceCard();
        if (existing is not null) Heading.Text = "编辑角色卡";
        CardName.Text = original.Name;
        Endpoint.Text = original.Endpoint;
        ReferenceAudio.Text = original.ReferenceAudio;
        Prompt.Text = original.PromptText;
        PromptLanguage.SelectedValue = original.PromptLanguage;
        Gpt.Text = original.GptWeights;
        Sovits.Text = original.SovitsWeights;
        Weights.IsExpanded = Gpt.Text.Length > 0 || Sovits.Text.Length > 0;
        Loaded += (_, _) => CardName.Focus();
    }

    private void SaveCard(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = original with
            {
                Name = CardName.Text.Trim(),
                Endpoint = GsvClient.BaseUri(Endpoint.Text.Trim()).AbsoluteUri,
                ReferenceAudio = ReferenceAudio.Text.Trim(),
                PromptText = Prompt.Text.Trim(),
                PromptLanguage = PromptLanguage.SelectedValue as string ?? "ja",
                GptWeights = Gpt.Text.Trim(),
                SovitsWeights = Sovits.Text.Trim()
            };
            GsvClient.ValidateCard(candidate);
            Card = candidate;
            DialogResult = true;
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
}
