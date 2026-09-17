using System.Windows;
using System.Runtime.InteropServices;
using System.Text;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public static class LocalizationService
{
    private const string ResourcePrefix = "Resources/Strings.";
    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.TraditionalChinese;
    public static event Action<AppLanguage>? LanguageChanged;

    public static void Apply(AppLanguage language)
    {
        CurrentLanguage = language;
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var old = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains(ResourcePrefix, StringComparison.OrdinalIgnoreCase) == true);
        if (old is not null) dictionaries.Remove(old);
        var name = language switch { AppLanguage.English => "en", AppLanguage.SimplifiedChinese => "zh-Hans", _ => "zh-Hant" };
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{name}.xaml", UriKind.Relative)
        });
        LanguageChanged?.Invoke(language);
    }

    public static string Get(string key)
    {
        var value = System.Windows.Application.Current.TryFindResource(key);
        return value?.ToString() ?? key;
    }

    public static string Pick(string traditionalChinese, string english) =>
        Translate(traditionalChinese, english, CurrentLanguage);

    internal static string Translate(string traditionalChinese, string english, AppLanguage language) =>
        language == AppLanguage.English ? english : language == AppLanguage.SimplifiedChinese ? ToSimplified(traditionalChinese) : traditionalChinese;

    internal static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        const uint simplifiedChinese = 0x02000000;
        var length = LCMapStringEx("zh-CN", simplifiedChinese, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0) return text;
        // Explicit source lengths do not guarantee a null-terminated destination.
        var result = new char[length];
        var written = LCMapStringEx("zh-CN", simplifiedChinese, text, text.Length, result, length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return written > 0
            ? new string(result, 0, written).Replace("後", "后").Replace("於", "于").Replace("裡", "里")
                .Replace("设定", "设置").Replace("储存", "保存").Replace("登入", "登录").Replace("预设", "默认")
            : text;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        [Out] char[]? destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);
}
