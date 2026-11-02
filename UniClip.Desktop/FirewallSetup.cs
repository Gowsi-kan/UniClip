using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UniClip.Desktop;

internal static class FirewallSetup
{
    private const int RendezvousPort = 5180;
    private const int DiscoveryPort = 5181;
    private const int PeerPort = 5182;

    public static string? Configure()
    {
        try
        {
            if (IsConfigured())
            {
                return null;
            }

            var result = OperatingSystem.IsWindows()
                ? ConfigureWindows()
                : OperatingSystem.IsLinux()
                    ? ConfigureLinux()
                    : FirewallResult.Unavailable;

            if (result == FirewallResult.Configured)
            {
                MarkConfigured();
                return null;
            }

            return result switch
            {
                FirewallResult.Cancelled =>
                    "Firewall setup was canceled. Allow TCP 5180/5182 and UDP 5181 through the firewall on this network.",
                FirewallResult.Failed =>
                    "Firewall setup failed. Allow TCP 5180/5182 and UDP 5181 through the firewall on this network.",
                FirewallResult.NoActiveFirewall =>
                    "No active UFW or firewalld detected. If pairing fails, allow TCP 5180/5182 and UDP 5181.",
                _ =>
                    "Automatic firewall setup is unavailable. Allow TCP 5180/5182 and UDP 5181 through the firewall on this network."
            };
        }
        catch (Exception exception)
        {
            return $"Firewall setup failed: {exception.Message}";
        }
    }

    private static FirewallResult ConfigureWindows()
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            if (-not (Get-NetFirewallRule -Name 'UniClipPrivateLanTcp' -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -Name 'UniClipPrivateLanTcp' -DisplayName 'UniClip local network (TCP)' -Group 'UniClip' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol TCP -LocalPort 5180,5182 -RemoteAddress LocalSubnet | Out-Null
            } else {
                Set-NetFirewallRule -Name 'UniClipPrivateLanTcp' -Profile Any -Enabled True | Out-Null
            }
            if (-not (Get-NetFirewallRule -Name 'UniClipPrivateLanUdp' -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -Name 'UniClipPrivateLanUdp' -DisplayName 'UniClip local network (UDP)' -Group 'UniClip' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol UDP -LocalPort 5181 -RemoteAddress LocalSubnet | Out-Null
            } else {
                Set-NetFirewallRule -Name 'UniClipPrivateLanUdp' -Profile Any -Enabled True | Out-Null
            }
            """;
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedScript}"
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return FirewallResult.Failed;
            }

            process.WaitForExit();
            return process.ExitCode == 0 ? FirewallResult.Configured : FirewallResult.Failed;
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return FirewallResult.Cancelled;
        }
    }

    private static FirewallResult ConfigureLinux()
    {
        var supportedFirewallFound = false;
        var ufw = FindExecutable("ufw");
        if (ufw is not null)
        {
            supportedFirewallFound = true;
            var status = Run(ufw, "status");
            if (status.ExitCode == 0 && status.Output.Contains("Status: active", StringComparison.OrdinalIgnoreCase))
            {
                var script = new StringBuilder("#!/bin/sh\nset -eu\n");
                AddUfwRules(script, ufw);
                return RunPrivilegedScript(script.ToString());
            }
        }

        var firewalld = FindExecutable("firewall-cmd");
        if (firewalld is not null)
        {
            supportedFirewallFound = true;
            var status = Run(firewalld, "--state");
            if (status.ExitCode == 0 && status.Output.Contains("running", StringComparison.OrdinalIgnoreCase))
            {
                var script = new StringBuilder("#!/bin/sh\nset -eu\n");
                AddFirewalldRules(script, firewalld);
                script.Append(Quote(firewalld)).AppendLine(" --reload");
                return RunPrivilegedScript(script.ToString());
            }
        }

        return supportedFirewallFound ? FirewallResult.NoActiveFirewall : FirewallResult.Unavailable;
    }

    private static void AddUfwRules(StringBuilder script, string ufw)
    {
        foreach (var subnet in PrivateSubnets)
        {
            script.Append(Quote(ufw)).Append(" allow from ").Append(subnet)
                .Append(" to any port ").Append(RendezvousPort)
                .AppendLine(" proto tcp comment 'UniClip LAN pairing'");
            script.Append(Quote(ufw)).Append(" allow from ").Append(subnet)
                .Append(" to any port ").Append(PeerPort)
                .AppendLine(" proto tcp comment 'UniClip LAN pairing'");
            script.Append(Quote(ufw)).Append(" allow from ").Append(subnet)
                .Append(" to any port ").Append(DiscoveryPort)
                .AppendLine(" proto udp comment 'UniClip LAN pairing'");
        }
    }

    private static void AddFirewalldRules(StringBuilder script, string firewalld)
    {
        foreach (var subnet in PrivateSubnets)
        {
            AddFirewalldRule(script, firewalld, subnet, RendezvousPort, "tcp");
            AddFirewalldRule(script, firewalld, subnet, PeerPort, "tcp");
            AddFirewalldRule(script, firewalld, subnet, DiscoveryPort, "udp");
        }
    }

    private static void AddFirewalldRule(
        StringBuilder script,
        string firewalld,
        string subnet,
        int port,
        string protocol)
    {
        var rule = $"rule family=\"ipv4\" source address=\"{subnet}\" port port=\"{port}\" protocol=\"{protocol}\" accept";
        script.Append(Quote(firewalld))
            .Append(" --permanent --add-rich-rule=")
            .AppendLine(Quote(rule));
    }

    private static FirewallResult RunPrivilegedScript(string script)
    {
        if (!OperatingSystem.IsLinux())
        {
            return FirewallResult.Unavailable;
        }

        var pkexec = FindExecutable("pkexec");
        if (pkexec is null)
        {
            return FirewallResult.Unavailable;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"unclip-firewall-{Guid.NewGuid():N}.sh");
        try
        {
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var result = Run(pkexec, "/bin/sh", scriptPath);
            if (result.ExitCode == 0)
            {
                return FirewallResult.Configured;
            }

            return result.ExitCode == 126 ? FirewallResult.Cancelled : FirewallResult.Failed;
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static (int ExitCode, string Output) Run(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return (-1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        return (process.ExitCode, $"{output.Result}\n{error.Result}");
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static bool IsConfigured() => File.Exists(GetMarkerPath());

    private static void MarkConfigured()
    {
        var markerPath = GetMarkerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "configured");
    }

    private static string GetMarkerPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(appData) ? Path.GetTempPath() : appData;
        return Path.Combine(root, "UniClip", "firewall-v2.configured");
    }

    private static string Quote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static readonly string[] PrivateSubnets =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "169.254.0.0/16"
    ];

    private enum FirewallResult
    {
        Configured,
        Cancelled,
        Failed,
        NoActiveFirewall,
        Unavailable
    }
}
