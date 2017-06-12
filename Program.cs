using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace FindWords
{
    class Program
    {
        const string Query = "SELECT Capacity FROM Win32_PhysicalMemory";
        static readonly ManagementObjectSearcher Searcher = new ManagementObjectSearcher(Query);

        static Program()
        {
            _currentProcess = Process.GetCurrentProcess();
        }

        static ulong GetMemInfo()
        {
            UInt64 Capacity = 0;
            foreach (ManagementObject wniPart in Searcher.Get())
                Capacity += Convert.ToUInt64(wniPart.Properties["Capacity"].Value);
            
            return Capacity;
        }

        private static Process _currentProcess;
        private static ulong GetAllocatedMemoryMb()
        {
            return (ulong)(_currentProcess.WorkingSet64 / Math.Pow(1024, 2));
        }

        private const int WORDS_LENGTH = 10; //мин. длина слов - условие
        private const int MAX_LENGHT = 1000; //макс. длина слов - можно меньше, тк таких слов не бывает
        private const int MAX_THREADS = 100; // для установки вручную кол-ва используемых потоков 
        private const int LINES_BUFFER = 20; // размер буфера строк из потока файла
        private const int THREAD_START_RANGE = 10;

        private static int hunksInWork = 0;

        private const int MEGABYTE = 1048576;
        static volatile bool readingFiles = true;
        static volatile int threads = 0;

        private static ConcurrentQueue<string> threadsOrder = new ConcurrentQueue<string>();
        private static ManualResetEventSlim threadsEnded = new ManualResetEventSlim(false);
        static List<ConcurrentQueue<string>> filesData = new List<ConcurrentQueue<string>>();

        private static Dictionary<string, int> allWords = new Dictionary<string, int>();
        static Stopwatch _stopwatch = new Stopwatch();

        [ThreadStatic]
        private static Dictionary<string, int> threadWords;

        [ThreadStatic]
        private static ISearchStrategy _search;

        static void Main()
        {
            // 1 поток читает диск
            // N разбирают текст

            #region init

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Debug.Listeners.Add(new TextWriterTraceListener(Console.Out));
            string folder = Environment.CurrentDirectory;
            folder = Directory.GetParent(folder).FullName;
            folder = Path.Combine(Directory.GetParent(folder).FullName, "Data");
            ulong memory = (ulong) (GetMemInfo() / MEGABYTE);
            int len = WORDS_LENGTH;
            if (len <= 0)
            {
                Console.WriteLine("введите длину слов:");
                string lenstr = Console.ReadLine();
                if (!int.TryParse(lenstr, out len) || len < 1)
                {
                    Console.WriteLine("некорректное число");
                    return;
                }
            }
            Console.WriteLine("папка: " + folder);
            Console.WriteLine("процессоры: " + Environment.ProcessorCount);
            Console.WriteLine($"память: {memory:N0} Mb");
            _search = GetSearchStrategy();
            #endregion

            Console.WriteLine("================ work ==============");
            _stopwatch.Start();
            var filenames = Directory.GetFiles(folder, "*.cs").ToList()
                .Concat(Directory.GetFiles(folder, "*.txt"))
                .ToArray();
            WriteInfo(null);
            int threadPooling = 0;

            using (Timer timer = new Timer(WriteInfo, null, 0, 200))
            {
                int prevFilesCheck = 0;
                string line;
                filesData.Add(new ConcurrentQueue<string>());
                StringBuilder sb = new StringBuilder(1000);
                int sbLine = 0;
                foreach (var file in filenames)
                {
                    using (StreamReader sr = File.OpenText(file))
                    {
                        while (true)
                        {
                            line = sr.ReadLine();
                            if (line == null && sbLine == 0)
                                break;
                            //буферизируем
                            if (line != null && sbLine++ < LINES_BUFFER)
                            {
                                sb.Append(line);
                                continue;
                            }
                            sbLine = 0;
                            
                            if (line != null)
                                sb.Append(line);
                            var sblines = sb.ToString();
                            sb.Clear();
                            ///////////////

                            int thread = 0;
                            if (threads > 0)
                                thread = threadPooling % threads;
                            
                            threadPooling = unchecked(++threadPooling) < 0 ? 0 : threadPooling;
                            filesData[thread].Enqueue(sblines);
                            Interlocked.Increment(ref hunksInWork);

                            if ((threads == 0 || threadPooling % THREAD_START_RANGE == 0 && hunksInWork >= prevFilesCheck + THREAD_START_RANGE) 
                                && threads < MAX_THREADS && threads < Environment.ProcessorCount)
                            {
                                prevFilesCheck = hunksInWork;
                                filesData.Add(new ConcurrentQueue<string>());
                                lock (threadsEnded)
                                    StartThread(threads);
                            }
                        }
                    }
                }
                readingFiles = false;
                if (filenames.Any())
                    threadsEnded.Wait();
                if (filesData.Any(x=>x.IsEmpty)) // мб с небольшой вероятностью
                    SearchWords();
            }

            Console.WriteLine();
            foreach (var thr in threadsOrder)
                Console.WriteLine(thr);

            var ordered = allWords.OrderByDescending(x => x.Value).Take(10).ToArray();
            _stopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine("всего cлов: " + allWords.Count);
            //Console.WriteLine("всего cтрок: " + threadPooling);
            Console.WriteLine();
            foreach (var wrd in ordered)
                Console.WriteLine($"{wrd.Key} : {wrd.Value}");
            
            Console.WriteLine();
            Console.WriteLine("max Mb allocated:" + (ulong) (_currentProcess.PeakWorkingSet64 / MEGABYTE));

            Debug.WriteLine($"time: {_stopwatch.Elapsed.TotalSeconds:N2}");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Debug.WriteLine(e.ToString());
        }

        private static int prevLen;
        private static void WriteInfo(object state)
        {
            ulong ws = GetAllocatedMemoryMb();
            string info = $"thrd cnt: {threads}, mem usage: {ws} Mb, hunksInWork: {hunksInWork}";

            Console.Write(new string(Enumerable.Repeat('\b', prevLen).ToArray()) + info);
           
            prevLen = info.Length;
        }

        private static void StartThread(int number)
        {
            threads++;
            threadsEnded.Reset();
            Thread t = new Thread(SearchWordsThread);
            t.Start(number);
        }

        private static void SearchWords()
        {
            var words = new Dictionary<string, int>();

            int thread = 0;
            string line;
            while (thread < filesData.Count)
            {
                if (filesData[thread].IsEmpty)
                {
                    thread++;
                    continue;
                }

                if (!filesData[thread].TryDequeue(out line) || line == null)
                    continue;

                hunksInWork--;
                _search.SearchInFile(line, words);
            }
            AddWords(words);
        }

        private static ISearchStrategy GetSearchStrategy()
        {
            return new SearchWordMaual(WORDS_LENGTH, MAX_LENGHT);
            //return new SearchWordRegex(WORDS_LENGTH, MAX_LENGHT);
        }

        private static void SearchWordsThread(object num)
        {
            int thread = 0;
            if(num is int)
                thread = (int) num;
            threadsOrder.Enqueue($"begin thread{thread}, milisec: {_stopwatch.ElapsedMilliseconds} linesInWork: {hunksInWork}");

            _search = GetSearchStrategy();

            SpinWait sw = new SpinWait();
            string line;
            threadWords = new Dictionary<string, int>();
            int threadLines = 0;
            while (!filesData[thread].IsEmpty || readingFiles)
            {
                if (filesData[thread].IsEmpty)
                {
                    sw.SpinOnce();
                    continue;
                }

                if (!filesData[thread].TryDequeue(out line) || line == null)
                    continue;
                Interlocked.Decrement(ref hunksInWork);
                threadLines++;
                _search.SearchInFile(line, threadWords);
            }
            
            lock (threadsEnded)
            {
                AddWords(threadWords);
                threadWords = null;
                threads--;
                if (threads <= 0)
                    threadsEnded.Set();
            }

            threadsOrder.Enqueue($"  end thread{thread}: milisec: {_stopwatch.ElapsedMilliseconds} linesInWork: {hunksInWork} spins: {sw.Count} thread lines: {threadLines}");
        }

        private static void AddWords(Dictionary<string, int> words)
        {
            foreach (var key in words.Keys)
            {
                int cnt = words[key];
                if (allWords.ContainsKey(key))
                    allWords[key] += cnt;
                else
                    allWords.Add(key, cnt);
            }
        }
    }
}
