using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace UniClip.Services;

public static class SessionDiagnostics
{
    public static bool Enabled { get; set; } = true;

    private static readonly object Sync = new();
    private static readonly Regex SixDigitCode = new(@"\b\d{6}\b", RegexOptions.Compiled);
    private static readonly Regex PairingInvite = new(@"unclip-pair:v1:[A-Za-z0-9_-]+", RegexOptions.Compiled);
    private static StreamWriter? _writer;

    public static string? LogFilePath { get; private set; }

    public static void StartSession()
    {
        if (!Enabled)
        {
            return;
        }

        lock (Sync)
        {
            if (_writer is not null)
            {
                return;
            }

            var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var fileName = $"unclip-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-pid{Environment.ProcessId}.log";
            LogFilePath = Path.Combine(logDirectory, fileName);
            _writer = new StreamWriter(
                new FileStream(LogFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
        }

        Write("Session", $"Started. OS={RuntimeInformation.OSDescription}; Framework={RuntimeInformation.FrameworkDescription}; Process={Path.GetFileName(Environment.ProcessPath)}; PID={Environment.ProcessId}; Version={Assembly.GetEntryAssembly()?.GetName().Version}; Log={Path.GetFileName(LogFilePath)}");
    }

    public static void Write(string category, string message, Exception? exception = null)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Sync)
        {
            if (_writer is null)
            {
                return;
            }

            var safeMessage = Redact(message);
            _writer.WriteLine($"{DateTimeOffset.UtcNow:O} [{category}] {safeMessage}");
            if (exception is not null)
            {
                _writer.WriteLine(Redact(exception.ToString()));
            }
        }
    }

    public static void EndSession()
    {
        lock (Sync)
        {
            if (_writer is null)
            {
                return;
            }

            Write("Session", "Ended.");
            _writer.Dispose();
            _writer = null;
        }
    }

    private static string Redact(string value) =>
        PairingInvite.Replace(SixDigitCode.Replace(value, "[REDACTED PIN]"), "[REDACTED INVITE]");
}
