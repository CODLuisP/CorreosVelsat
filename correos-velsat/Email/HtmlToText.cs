using System.Net;
using System.Text.RegularExpressions;

namespace CorreosVelsat.Email
{
    /// <summary>
    /// Conversión básica de HTML a texto plano para la parte "text" del correo.
    /// </summary>
    public static partial class HtmlToText
    {
        [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex HiddenBlocks();

        [GeneratedRegex(@"<a\b[^>]*\bhref\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex Links();

        [GeneratedRegex(@"<br\s*/?>|</(p|div|h[1-6]|tr|table|ul|ol|blockquote)\s*>", RegexOptions.IgnoreCase)]
        private static partial Regex LineBreaks();

        [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex ListItems();

        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex Tags();

        [GeneratedRegex(@"[ \t\f\v]+")]
        private static partial Regex Spaces();

        [GeneratedRegex(@"\n{3,}")]
        private static partial Regex BlankLines();

        public static string Convert(string html)
        {
            string text = html.Replace("\r\n", "\n").Replace('\r', '\n');
            text = HiddenBlocks().Replace(text, "");
            text = Links().Replace(text, m =>
            {
                string href = m.Groups[1].Value;
                string label = Tags().Replace(m.Groups[2].Value, "").Trim();
                return label.Length == 0 || label == href ? href : $"{label} ({href})";
            });
            text = text.Replace("\n", " ");
            text = LineBreaks().Replace(text, "\n");
            text = ListItems().Replace(text, "\n- ");
            text = Tags().Replace(text, "");
            text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
            text = Spaces().Replace(text, " ");
            text = string.Join('\n', text.Split('\n').Select(line => line.Trim()));
            text = BlankLines().Replace(text, "\n\n");
            return text.Trim();
        }
    }
}
