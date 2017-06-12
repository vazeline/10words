using System.Collections.Generic;

namespace FindWords
{
    interface ISearchStrategy
    {
        void SearchInFile(string content, IDictionary<string, int> words);
    }
}
