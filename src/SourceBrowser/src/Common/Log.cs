using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.SourceBrowser.Common
{
    public static class Log
    {
        public const string ErrorLogFile = "Errors.txt";
        public const string MessageLogFile = "Messages.txt";
        private const string SeparatorBar = "===============================================";

        private static string errorLogFilePath = Path.GetFullPath(ErrorLogFile);
        private static string messageLogFilePath = Path.GetFullPath(MessageLogFile);

        private static TaskCompletionSource<object> completedTask = new TaskCompletionSource<object>();
        private static readonly BlockingCollection<IMessage> Messages = new BlockingCollection<IMessage>();

        private static readonly Thread loggerThread;

        private static int errorCount;

        // Number of severe errors logged so far. HtmlGenerator uses this to return a non-zero exit code
        // when indexing hit a real failure, so CI catches broken runs instead of treating them as success.
        public static int ErrorCount => Volatile.Read(ref errorCount);

        private static void OnNext(IMessage msg)
        {
            switch (msg)
            {
                case ConsoleMessage consoleMessage:
                    InnerWrite(consoleMessage.Message, consoleMessage.Color);
                    break;
                case FileMessage fileMessage:
                    InnerWriteToFile(fileMessage.Message, fileMessage.FilePath);
                    break;
            }
        }

        static Log()
        {
            loggerThread = new Thread(ProcessMessages)
            {
                IsBackground = true,
                Name = "ThreadLogger",
            };
            loggerThread.Start();
        }

        private static void ProcessMessages()
        {
            foreach (var message in Messages.GetConsumingEnumerable())
            {
                OnNext(message);
            }

            OnCompleted();
        }

        public static Task WaitForCompletion()
        {
            return completedTask.Task;
        }

        private static void OnCompleted()
        {
            completedTask.SetResult(null);
        }

        private static void Enqueue(IMessage message)
        {
            try
            {
                Messages.Add(message);
            }
            catch (InvalidOperationException)
            {
                // Logging has been closed via Close(); drop the message.
            }
        }

        public static void Exception(Exception e, string message, bool isSevere = true)
        {
            var text = message + Environment.NewLine + e.ToString();
            Exception(text, isSevere);
        }

        // When set, non-severe warnings (e.g. first-chance exception noise from MSBuild evaluation) are
        // dropped from both the console and Errors.txt so the logs stay readable. Severe errors are
        // always kept. Wired from the /noWarnings command-line switch.
        public static bool SuppressWarnings { get; set; }

        public static void Exception(string message, bool isSevere = true)
        {
            if (isSevere)
            {
                Interlocked.Increment(ref errorCount);
            }
            else if (SuppressWarnings)
            {
                return;
            }

            // Tag the severity into the message text itself so it survives redirection: the console
            // color (Red vs Yellow) is lost once output is captured to a file or CI log, leaving severe
            // errors -- the ones that increment ErrorCount and drive HtmlGenerator's non-zero exit --
            // indistinguishable from benign first-chance noise. The tag makes them greppable everywhere.
            string tagged = (isSevere ? "[SEVERE] " : "[WARN] ") + message;
            Write(tagged, isSevere ? ConsoleColor.Red : ConsoleColor.Yellow);
            WriteToFile(tagged, ErrorLogFilePath);
        }

        public static void Message(string message)
        {
            Write(message, ConsoleColor.Blue);
            WriteToFile(message, MessageLogFilePath);
        }
        
        private static void WriteToFile(string message, string filePath)
        {
            Enqueue(new FileMessage(message, filePath));
        }

        private static void InnerWriteToFile(string message, string filePath)
        {
            try
            {
                File.AppendAllText(filePath, SeparatorBar + Environment.NewLine + message + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Write($"Failed to write to ${filePath}: ${ex}.", ConsoleColor.Red);
            }
        }

        public static void Write(string message, ConsoleColor color = ConsoleColor.Gray)
        {
            Enqueue(new ConsoleMessage(message, color));
        }

        private static void InnerWrite(string message, ConsoleColor color = ConsoleColor.Gray)
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(DateTime.Now.ToString("HH:mm:ss") + " ");
                Console.ForegroundColor = color;
                Console.WriteLine(message);
                if (color != ConsoleColor.Gray)
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        public static string ErrorLogFilePath
        {
            get { return errorLogFilePath; }
            set { errorLogFilePath = value.MustBeAbsolute(); }
        }

        public static string MessageLogFilePath
        {
            get { return messageLogFilePath; }
            set { messageLogFilePath = value.MustBeAbsolute(); }
        }

        // Stop accepting new messages and block until the background thread has drained everything
        // already queued. The logger thread is a background thread, so without this join a fast exit
        // (notably argument-validation failures that return before any indexing work) would let the
        // process terminate before the queued messages are ever written to the console or log files.
        // See https://github.com/KirillOsenkov/SourceBrowser/issues/165.
        public static void Close()
        {
            if (!Messages.IsAddingCompleted)
            {
                Messages.CompleteAdding();
            }

            loggerThread.Join();
        }
    }

    internal interface IMessage
    {
        string Message { get; }
    }

    internal class ConsoleMessage : IMessage
    {
        public string Message { get; }
        public ConsoleColor Color { get; }
        public ConsoleMessage(string message, ConsoleColor color)
        {
            Message = message;
            Color = color;
        }
    }

    internal class FileMessage : IMessage
    {
        public string Message { get; }
        public string FilePath { get; }
        public FileMessage(string message, string filePath)
        {
            Message = message;
            FilePath = filePath;
        }
    }

}
