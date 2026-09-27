using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Views;

public sealed class ConversationPreviewBox : System.Windows.Controls.RichTextBox
{
    private string _text = "";
    private BrowserPreviewPage? _page;
    public string Text
    {
        get => _text;
        set { _page = null; _text = value; Document.Blocks.Clear(); Document.Blocks.Add(new Paragraph(new Run(value))); }
    }
    public ConversationPreviewBox()
    {
        IsReadOnly = true; IsUndoEnabled = false;
        VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 13;
        Background = Brush("#F3F7F9"); BorderThickness = new Thickness(0);
        Document = new FlowDocument { PagePadding = new Thickness(14) };
        HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
    }
    public void Clear() { _page = null; _text = ""; Document.Blocks.Clear(); }
    public void SetLanguage(AppLanguage language) { if (_page is not null) ShowPage(_page, language); }
    public void ShowPage(BrowserPreviewPage page, AppLanguage language)
    {
        _page = page; _text = page.Text; Document.Blocks.Clear();
        foreach (var part in page.Parts)
        {
            var user = part.Role == "user";
            var section = new Section { Background = Brush(user ? "#E0EEF4" : "#FFFFFF"), Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(user ? 32 : 0, 0, user ? 0 : 32, 12), BorderBrush = Brush(user ? "#C3DCE7" : "#DFE7EC"), BorderThickness = new Thickness(1) };
            var label = user ? LocalizationService.Translate("你", "You", language) : "AI";
            if (part.Continued) label += LocalizationService.Translate("（續）", " (continued)", language);
            section.Blocks.Add(new Paragraph(new Run(label + (part.Timestamp.Length > 0 ? "  ·  " + part.Timestamp : "")))
                { Foreground = Brush("#537C91"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
            section.Blocks.Add(new Paragraph(new Run(part.Text)) { Foreground = Brush("#243B49"), Margin = new Thickness(0), LineHeight = 20 });
            Document.Blocks.Add(section);
        }
        ScrollToHome();
    }
    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush;
    }
}
