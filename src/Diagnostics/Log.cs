using System.Text;

namespace XBridge.Diagnostics;

internal enum LogLevel { Info, Warn, Error }

// Rolling, size-capped log under %LocalAppData%\XBridge\logs. Thread-safe;
// safe to call before Init (messages are buffered).
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly List<string> PreInitBuffer = new();

    private static string _dir = "";
    private static string _file = "";
    private static long _maxBytes = 1_048_576;
    private static int _maxFiles = 5;
    private static bool _initialised;
    private static bool _mirrorToConsole;

    public static void Init()
    {
        lock (Gate)
        {
            try
            {
                _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "XBridge", "logs");
                System.IO.Directory.CreateDirectory(_dir);
                _file = Path.Combine(_dir, "app.log");
                _initialised = true;

                WriteLine(Format(LogLevel.Info, $"===== log opened (pid {Environment.ProcessId}) ====="));
                foreach (var line in PreInitBuffer) WriteLine(line);
                PreInitBuffer.Clear();
            }
            catch { _initialised = false; }
        }
    }

    public static void Configure(long maxBytes, int maxFiles)
    {
        lock (Gate)
        {
            _maxBytes = Math.Max(64 * 1024, maxBytes);
            _maxFiles = Math.Clamp(maxFiles, 1, 50);
        }
    }

    public static void EnableConsoleMirror() => _mirrorToConsole = true;

    public static void Info(string m) => Emit(LogLevel.Info, m);
    public static void Warn(string m) => Emit(LogLevel.Warn, m);
    public static void Error(string m) => Emit(LogLevel.Error, m);

    public static void Error(string m, Exception? ex) =>
        Emit(LogLevel.Error, ex is null ? m : $"{m} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");

    private static void Emit(LogLevel level, string message)
    {
        string line = Format(level, message);
        lock (Gate)
        {
            if (_mirrorToConsole) { try { Console.WriteLine(line); } catch { } }

            if (!_initialised)
            {
                PreInitBuffer.Add(line);
                if (PreInitBuffer.Count > 500) PreInitBuffer.RemoveAt(0);
                return;
            }

            try { RollIfNeeded(); WriteLine(line); }
            catch { }   // never let logging throw
        }
    }

    private static string Format(LogLevel level, string message)
    {
        string tag = level switch { LogLevel.Info => "INF", LogLevel.Warn => "WRN", _ => "ERR" };
        return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] [t{Environment.CurrentManagedThreadId:D2}] {message}";
    }

    private static void WriteLine(string line) =>
        File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);

    // app.log -> app.1.log -> ... -> app.(N-1).log ; oldest dropped
    private static void RollIfNeeded()
    {
        var fi = new FileInfo(_file);
        if (!fi.Exists || fi.Length < _maxBytes) return;

        string Slot(int i) => Path.Combine(_dir, i == 0 ? "app.log" : $"app.{i}.log");

        try { File.Delete(Slot(_maxFiles - 1)); } catch { }
        for (int i = _maxFiles - 2; i >= 0; i--)
        {
            string src = Slot(i);
            if (File.Exists(src)) { try { File.Move(src, Slot(i + 1), overwrite: true); } catch { } }
        }
    }
}
