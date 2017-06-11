using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

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
        private const int MAX_THREADS = 1000; // для установки вручную кол-ва используемых потоков 
        private const int MEM_PRESSURE = 200; // мб памяти доступно

        private const int MEGABYTE = 1048576;
        static volatile bool readingFiles = true;
        static volatile int threads = 0;

        private static ConcurrentQueue<string> threadsOrder = new ConcurrentQueue<string>();
        private static ManualResetEventSlim threadsEnded = new ManualResetEventSlim(false);
        static ConcurrentQueue<Tuple<string, string>> files = new ConcurrentQueue<Tuple<string, string>>();

        private static Dictionary<string, int> allWords = new Dictionary<string, int>();
        static Stopwatch _stopwatch = new Stopwatch();

        [ThreadStatic]
        private static Dictionary<string, int> threadWords;

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
            ulong memory = (ulong)(GetMemInfo() / MEGABYTE);
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
            
            #endregion

            Console.WriteLine("================ work ==============");
            _stopwatch.Start();
            var filenames = Directory.GetFiles(folder, "*.cs").ToList()
                .Concat(Directory.GetFiles(folder,"*.txt"))
                .ToArray();
            WriteInfo(null);
            
            using (Timer timer = new Timer(WriteInfo,null, 0,200))
            {
                int prevFilesCheck = 0;
                foreach (var file in filenames)
                {
                    //TODO: большие файлы нужно читать в потоке последовательно и последовательно разбирать

                    files.Enqueue(new Tuple<string, string>(File.ReadAllText(file), file));

                    if ((filenames.Length < 5 || files.Count > prevFilesCheck + 5) && threads < MAX_THREADS)
                    {
                        prevFilesCheck = files.Count;

                        lock (threadsEnded)
                            if (files.Any() && threads <= Environment.ProcessorCount)
                                StartThread();
                    }
                    else if (GetAllocatedMemoryMb() > MEM_PRESSURE) //todo: если необходимо, приостанавливать чтение
                    {
                       Thread.Sleep(10);
                    }
                }
                readingFiles = false;
                if(filenames.Any())
                    threadsEnded.Wait();
                if (files.Any()) // мб с небольшой вероятностью
                {
                    StartThread();
                    threadsEnded.Wait();
                }
            }
            
            Console.WriteLine();
            foreach (var thr in threadsOrder)
                Console.WriteLine(thr);

            var ordered = allWords.OrderByDescending(x => x.Value).Take(10).ToArray();
            _stopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine("всего cлов: " + allWords.Count);
            Console.WriteLine();
            foreach (var wrd in ordered)
            {
                Console.WriteLine($"{wrd.Key} : {wrd.Value}");
            }
            Console.WriteLine();
            Console.WriteLine("max Mb allocated:"+(ulong)(_currentProcess.PeakWorkingSet64 / MEGABYTE));
            
            Debug.WriteLine($"time: {_stopwatch.Elapsed.TotalSeconds:N2}");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Debug.WriteLine(e.ToString());
        }

        private static int prevLen = 0;
        private static void WriteInfo(object state)
        {
            ulong ws = GetAllocatedMemoryMb();
            string info = $"thrd cnt: {threads}, mem usage: {ws} Mb, files: {files.Count}";

            Console.Write(new string(Enumerable.Repeat('\b', prevLen).ToArray()) + info);
           
            prevLen = info.Length;
        }

        private static void StartThread()
        {
            threads++;
            threadsEnded.Reset();
            ThreadPool.UnsafeQueueUserWorkItem(SearchWords, null);
        }

        private static void SearchWords(object state)
        {
            SpinWait sw = new SpinWait();
            Tuple<string,string> fileItem;
            threadWords = new Dictionary<string, int>();

            ISearchStrategy search = new SearchWordMaual(WORDS_LENGTH, MAX_LENGHT); //в >3 раза быстрее
            //ISearchStrategy search = new SearchWordRegex(WORDS_LENGTH, MAX_LENGHT);

            threadsOrder.Enqueue($"begin thread, milisec: {_stopwatch.ElapsedMilliseconds} files: {files.Count}");
            
            while (!files.IsEmpty || readingFiles)
            {
                if (files.IsEmpty)
                {
                    sw.SpinOnce();
                    continue;
                }

                if (!files.TryDequeue(out fileItem) || fileItem == null)
                    continue;

                search.SearchInFile(content: fileItem.Item1, filename: fileItem.Item2, words: threadWords);
            }
            
            lock (threadsEnded)
            {
                foreach (var key in threadWords.Keys)
                {
                    int cnt = threadWords[key];
                    if (allWords.ContainsKey(key))
                    {
                        allWords[key] += cnt;
                    }
                    else
                        allWords.Add(key, cnt);
                }
                threadWords = null;
                threads--;
                if (threads <= 0)
                    threadsEnded.Set();
            }

            threadsOrder.Enqueue($"  end thread: milisec: {_stopwatch.ElapsedMilliseconds} files: {files.Count}");
        }
    }
}
