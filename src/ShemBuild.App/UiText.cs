using System.Globalization;

namespace ShemBuild.App;

internal static class UiText
{
    public static bool IsEnglish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
        .Equals("en", StringComparison.OrdinalIgnoreCase);

    public static string Get(string ukrainian, string english) => IsEnglish ? english : ukrainian;
}
