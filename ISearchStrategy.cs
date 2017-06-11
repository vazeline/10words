using System.Collections.Generic;

namespace FindWords
{
    interface ISearchStrategy
    {
        void SearchInFile(string content, string filename, IDictionary<string, int> words);
    }
}
