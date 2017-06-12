using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FindWords
{
    class SearchWordRegex : ISearchStrategy
    {
        private Regex regex;
        public SearchWordRegex(int minlen, int maxlen)
        {
            regex = new Regex(@"\w{" + minlen + "," + maxlen + "}", RegexOptions.Compiled);
        }

        public void SearchInFile(string content, IDictionary<string, int> words)
        {
            var matches = regex.Matches(content);
            foreach (Match match in matches)
            {
                if (!words.ContainsKey(match.Value))
                    words.Add(match.Value, 1);
                else
                    words[match.Value]++;
            }
        }
    }
}
