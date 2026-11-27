using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DualMeter
{
    internal static class GeminiSource
    {
        private sealed class QuotaError : InvalidOperationException
        {
            internal QuotaError(string message) : base(message) { }
        }

        public static List<Reading> Fetch()
        {
            try
            {
                string cli = FindCli();
                string versionText = Run(cli, "--version", 8000);
                var match = Regex.Match(versionText, @"(?m)^\s*(?:agy\s+)?v?(\d+\.\d+\.\d+)\b");
                Version version;
                // Earlier CLI versions treated /usage as a model prompt.
                if (!match.Success || !Version.TryParse(match.Groups[1].Value, out version) || version < new Version(1, 1, 11))
                    throw new QuotaError("请更新 Antigravity CLI 至 1.1.11 或更高版本。");
                return Parse(Run(cli, "-p /usage --disable-slash-commands=false --output-format text --print-timeout 20s", 35000));
            }
            catch (QuotaError) { throw; }
            catch (Exception) { throw new QuotaError("无法读取额度，请在终端运行 agy -p /usage 检查。"); }
        }

        private static string FindCli()
        {
            string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
            if (File.Exists(standard)) return standard;
            foreach (string item in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string dir = Environment.ExpandEnvironmentVariables(item.Trim().Trim('"'));
                if (!string.IsNullOrEmpty(dir) && Path.IsPathRooted(dir))
                {
                    string path = Path.Combine(dir, "agy.exe");
                    if (File.Exists(path)) return path;
                }
            }
            throw new QuotaError("请安装 Antigravity CLI，运行 agy 登录。");
        }

        private static string Run(string cli, string arguments, int timeout)
        {
            var start = new ProcessStartInfo(cli, arguments) {
                WorkingDirectory = Path.GetDirectoryName(cli), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            // Quota polls must not spawn the CLI's separate periodic update process.
            start.EnvironmentVariables["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
            using (var process = Process.Start(start))
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    if (error.Wait(1000) && NeedsLogin(error.Result)) throw LoginRequired();
                    throw new QuotaError("额度查询超时，请检查网络或代理。");
                }
                if (!Task.WaitAll(new Task[] { output, error }, 2000))
                    throw new QuotaError("Antigravity CLI 响应超时。");
                if (process.ExitCode != 0)
                {
                    if (NeedsLogin(error.Result + "\n" + output.Result)) throw LoginRequired();
                    throw new QuotaError("额度读取失败，可在 agy 中输入 /usage 查看。");
                }
                return output.Result;
            }
        }

        private static bool NeedsLogin(string message)
        {
            return Regex.IsMatch(message, @"authenticat(?:ion required|ion failed|e in)|unauthenticated|not logged in|sign in|oauth|invalid_grant", RegexOptions.IgnoreCase);
        }

        private static Exception LoginRequired()
        {
            return new QuotaError("请在终端运行 agy，登录后刷新。");
        }

        internal static List<Reading> Parse(string report)
        {
            // Group by the provider's own pool and window; a disabled or unreported value is never zero.
            var groups = new SortedDictionary<string, SortedDictionary<int, Reading>>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in report.Split('\n'))
            {
                string[] fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length < 3) continue;
                string pool = fields[0].Trim();
                bool gemini = Regex.IsMatch(pool, @"^Gemini\b", RegexOptions.IgnoreCase);
                bool other = Regex.IsMatch(pool, @"^Claude (?:and|&) GPT(?: models)?$", RegexOptions.IgnoreCase);
                if (!gemini && !other) continue;
                string window = fields[1].Trim();
                if (!Regex.IsMatch(window, @"\b(?:limit|quota)\s+remaining\b", RegexOptions.IgnoreCase)) continue;
                bool fiveHours = Regex.IsMatch(window, @"^(?:five|5)[ -]hour\b", RegexOptions.IgnoreCase);
                if (!fiveHours && !window.StartsWith("Weekly", StringComparison.OrdinalIgnoreCase)) continue;

                string name = other ? "Claude / GPT" : Regex.Replace(pool, @"\s+Models?$", "", RegexOptions.IgnoreCase);
                // Keep the shared Gemini pool before any model-specific windows, and Claude/GPT last.
                string groupKey = (name.Equals("Gemini", StringComparison.OrdinalIgnoreCase) ? "0" : gemini ? "1" : "2") + name;
                SortedDictionary<int, Reading> rows;
                if (!groups.TryGetValue(groupKey, out rows)) groups.Add(groupKey, rows = new SortedDictionary<int, Reading>());
                string value = fields[2].Trim();
                int? percent = null;
                decimal parsed;
                if (value.EndsWith("%", StringComparison.Ordinal) && decimal.TryParse(value.Substring(0, value.Length - 1),
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed) && parsed >= 0 && parsed <= 100)
                    percent = (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
                string reset = value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                    ? "未启用" : FormatReset(fields.Length > 3 ? fields[3].Trim() : "");
                rows[fiveHours ? 0 : 1] = new Reading((fiveHours ? "5 小时" : "每周") + " · " + name, percent, reset);
            }
            if (groups.Count == 0) throw new QuotaError("官方查询尚未返回可识别的额度。");
            var results = new List<Reading>();
            foreach (var group in groups)
            {
                if (results.Count == 0 && (group.Key[0] == '2' || !group.Value.ContainsKey(0)))
                    results.Add(new Reading("5 小时 · Gemini", null, "暂未提供"));
                results.AddRange(group.Value.Values);
            }
            return results;
        }

        private static string FormatReset(string text)
        {
            if (text.Length == 0) return "重置时间未知";
            DateTimeOffset reset;
            if (!Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)
                || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out reset))
                return "重置时间未知";
            DateTime local = reset.LocalDateTime;
            string format = local.Date == DateTime.Today ? "HH:mm" : "M月d日 HH:mm";
            return local.ToString(format, CultureInfo.InvariantCulture) + " 重置";
        }
    }
}
