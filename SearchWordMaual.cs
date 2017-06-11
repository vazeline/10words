using System;
using System.Collections.Generic;
using System.Text;

namespace FindWords
{
    class SearchWordMaual : ISearchStrategy
    {
        private int _maxLength;
        private int _minLength;

        public SearchWordMaual(int minlen,int maxlen)
        {
            _minLength = minlen;
            _maxLength = maxlen;
        }

        public unsafe void SearchInFile(string content, string filename, IDictionary<string, int> words)
        {
            StringBuilder bigWord = new StringBuilder(_maxLength);
            fixed (char* chrs = content)
            {
                char* word = stackalloc char[50];
                string strword;
                int wlen = 0;
                for (int i = 0; i < content.Length; i++)
                {
                    var c = chrs[i];
                    if (Char.IsLetter(c) || Char.IsDigit(c) || c == '_')
                    {
                        if (wlen >= 49) //большое слово, используем обычный массив
                        {
                            if (wlen >= _maxLength) //ошибка ?
                            {
                                bigWord.Clear();
                                Console.WriteLine("ошибка в файле " + filename);
                                break;
                            }

                            if (wlen == 49)
                            {
                                bigWord.Clear();
                                bigWord.Append(new string(word));
                            }
                            bigWord.Append(c);
                        }
                        else
                        {
                            word[wlen++] = c;
                        }
                    }
                    else // слово закончилось
                    {
                        if (wlen < _minLength)
                            wlen = 0;

                        if (wlen > 0)
                        {
                            //запомнить слово
                            if (wlen <= 49)
                            {
                                word[wlen] = '\0';
                                strword = new string(word);
                            }
                            else
                                strword = bigWord.ToString();

                            if (!words.ContainsKey(strword))
                                words.Add(strword, 1);
                            else
                                words[strword]++;

                            wlen = 0;
                        }
                    }
                }
            }
        }
    }
}
