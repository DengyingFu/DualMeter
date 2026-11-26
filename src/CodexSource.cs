using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DualMeter
{
    internal static class CodexSource
    {
        private sealed class QuotaError : InvalidOperationException
        {
            internal QuotaError(string message) : base(message) { }
        }

        public static List<Reading> Fetch()
        {
            try
            {
                string executable = FindCodex();
                if (executable == null) throw new QuotaError("请先安装或更新 Codex 客户端");
                return Parse(Query(executable));
            }
            catch (QuotaError) { throw; }
            catch (Exception) { throw new QuotaError("无法读取额度，请检查 Codex 登录状态"); }
        }

        private static string FindCodex()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string desktop = Path.Combine(local, "OpenAI", "Codex", "bin");
            if (Directory.Exists(desktop))
            {
                string bundled = Directory.GetDirectories(desktop)
                    .Select(d => Path.Combine(d, "codex.exe")).Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (bundled != null) return bundled;
            }
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string vendor = Path.Combine(roaming, @"npm\node_modules\@openai\codex\node_modules\@openai");
            foreach (string platform in new[] { "x64", "arm64" })
            {
                string triple = platform == "x64" ? "x86_64-pc-windows-msvc" : "aarch64-pc-windows-msvc";
                string cli = Path.Combine(vendor, "codex-win32-" + platform, "vendor", triple, "bin", "codex.exe");
                if (File.Exists(cli)) return cli;
            }
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string directory = entry.Trim().Trim('"');
                if (String.IsNullOrEmpty(directory) || !Path.IsPathRooted(directory)) continue;
                string path = Path.Combine(directory, "codex.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static Dictionary<string, object> Query(string executable)
        {
            var initialized = new TaskCompletionSource<Dictionary<string, object>>();
            var quota = new TaskCompletionSource<Dictionary<string, object>>();
            var json = new JavaScriptSerializer();
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable, "app-server --listen stdio://") {
                    WorkingDirectory = Path.GetTempPath(), CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs data)
                {
                    if (data.Data == null)
                    {
                        initialized.TrySetResult(null);
                        quota.TrySetResult(null);
                        return;
                    }
                    Dictionary<string, object> message;
                    try { message = json.DeserializeObject(data.Data) as Dictionary<string, object>; }
                    catch (Exception) { return; }
                    string id = Convert.ToString(Get(message, "id"), CultureInfo.InvariantCulture);
                    if (id == "1") initialized.TrySetResult(message);
                    if (id == "2") quota.TrySetResult(message);
                };
                process.ErrorDataReceived += delegate { };
                if (!process.Start()) throw new QuotaError("无法启动 Codex 客户端");
                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var input = process.StandardInput;
                    input.AutoFlush = true;
                    input.WriteLine("{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"quota-meter\",\"title\":\"Quota Meter\",\"version\":\"1.0\"}}}");
                    Result(initialized.Task, 10000);
                    input.WriteLine("{\"method\":\"initialized\"}");
                    input.WriteLine("{\"method\":\"account/rateLimits/read\",\"id\":2,\"params\":{}}");
                    return Result(quota.Task, 25000);
                }
                finally
                {
                    try { process.StandardInput.Close(); } catch (IOException) { }
                    if (!process.WaitForExit(1000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        process.WaitForExit(1500);
                    }
                }
            }
        }

        private static Dictionary<string, object> Result(Task<Dictionary<string, object>> reply, int timeout)
        {
            if (!reply.Wait(timeout)) throw new QuotaError("查询超时，请稍后刷新");
            var result = Get(reply.Result, "result") as Dictionary<string, object>;
            if (result != null) return result;
            var error = Get(reply.Result, "error") as Dictionary<string, object>;
            string description = (Get(error, "message") as string ?? "").ToLowerInvariant();
            if (new[] { "401", "sign in", "login", "not authenticated", "unauthorized", "api key" }.Any(description.Contains))
                throw new QuotaError("请使用 ChatGPT 账号登录 Codex 后刷新");
            if (description.Contains("method not found")) throw new QuotaError("请更新 Codex 客户端后刷新");
            throw new QuotaError("Codex 暂时无法提供额度，请稍后刷新");
        }

        private static object Get(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value : null;
        }

        internal static List<Reading> Parse(Dictionary<string, object> result)
        {
            var byId = Get(result, "rateLimitsByLimitId") as Dictionary<string, object>;
            var standard = Get(byId, "codex") as Dictionary<string, object>;
            var reported = Get(result, "rateLimits") as Dictionary<string, object>;
            foreach (var candidate in new[] { standard, reported })
            {
                var rows = new List<KeyValuePair<int, Reading>>();
                foreach (string name in new[] { "primary", "secondary" })
                {
                    var window = Get(candidate, name) as Dictionary<string, object>;
                    if (window == null) continue;
                    int minutes;
                    Int32.TryParse(Convert.ToString(Get(window, "windowDurationMins"), CultureInfo.InvariantCulture), out minutes);
                    string label = name == "primary" ? "主额度" : "次额度";
                    if (minutes == 10080) label = "每周";
                    else if (minutes > 0 && minutes % 1440 == 0) label = (minutes / 1440) + " 天";
                    else if (minutes > 0 && minutes % 60 == 0) label = (minutes / 60) + " 小时";
                    else if (minutes > 0) label = minutes + " 分钟";
                    decimal used;
                    string text = Convert.ToString(Get(window, "usedPercent"), CultureInfo.InvariantCulture);
                    int? remaining = Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out used) && used >= 0 && used <= 100
                        ? (int?)Decimal.Round(100 - used, 0, MidpointRounding.AwayFromZero) : null;
                    rows.Add(new KeyValuePair<int, Reading>(minutes > 0 ? minutes : Int32.MaxValue,
                        new Reading(label, remaining, FormatReset(Get(window, "resetsAt")))));
                }
                if (rows.Count != 0) return rows.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
            }
            throw new QuotaError("当前 Codex 账号未提供额度窗口");
        }

        private static string FormatReset(object value)
        {
            long epoch;
            if (!Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out epoch) || epoch <= 0 || epoch > 253402214399)
                return "重置时间未知";
            var local = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch).ToLocalTime();
            return local.ToString(local.Date == DateTime.Today ? "HH:mm" : "M月d日 HH:mm", CultureInfo.InvariantCulture) + " 重置";
        }
    }
}
