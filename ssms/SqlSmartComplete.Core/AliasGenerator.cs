using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SqlSmartComplete
{
    /// <summary>
    /// dbForge-style aliases: first letter of each word. Customers -> c,
    /// SalesOrderHeader -> soh, order_details -> od. Clashes get a number: c1, c2.
    /// </summary>
    public static class AliasGenerator
    {
        private static readonly Regex Separators = new Regex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
        private static readonly Regex Words = new Regex(@"\p{Lu}+(?=\p{Lu}\p{Ll})|\p{Lu}?\p{Ll}+|\p{Lu}+|\p{N}+|\p{L}+", RegexOptions.Compiled);

        public static List<string> SplitWords(string name)
        {
            var words = new List<string>();
            foreach (var part in Separators.Split(name.TrimStart('#')).Where(p => p.Length > 0))
            {
                var ms = Words.Matches(part);
                if (ms.Count == 0) words.Add(part);
                else foreach (Match m in ms) words.Add(m.Value);
            }
            return words;
        }

        public static string BaseAlias(string name)
        {
            var letters = string.Concat(SplitWords(name).Where(w => char.IsLetter(w[0])).Select(w => w[0]));
            return (letters.Length > 0 ? letters : "t").ToLowerInvariant();
        }

        public static string Make(string name, ISet<string> used, bool upper = false)
        {
            string b = BaseAlias(name);
            string alias = b;
            for (int n = 1; used.Contains(alias) || SqlKeywords.AliasStop.Contains(alias); n++) alias = b + n;
            return upper ? alias.ToUpperInvariant() : alias;
        }
    }
}
