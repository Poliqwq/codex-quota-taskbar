using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexQuotaTaskbar
{
    public class QuotaReading
    {
        public double RemainingPercent;
        public double UsedPercent;
        public DateTimeOffset? ResetsAt;
        public string LimitId;
        public string PlanType;
        public DateTimeOffset FetchedAt;
        public string Error;
        public bool IsStale;
    }

    // Queries account metadata only. This client never starts a thread or a model turn.
    public sealed class QuotaClient : IDisposable
    {
        private const int TimeoutMilliseconds = 25000;
        private readonly string _codexExe;
        private readonly object _gate = new object();
        private Process _activeProcess;
        private Task _refreshTask;
        private QuotaReading _lastGoodReading;
        private bool _disposed;

        public event Action<QuotaReading> Updated;

        public QuotaClient(string codexExe)
        {
            if (String.IsNullOrWhiteSpace(codexExe))
                throw new ArgumentException("A Codex executable path is required.", "codexExe");
            _codexExe = codexExe;
        }

        // Concurrent refresh requests share one operation. Updated runs on a worker thread.
        public Task RefreshAsync()
        {
            lock (_gate)
            {
                if (_disposed)
                    return Task.FromResult(0);
                if (_refreshTask != null && !_refreshTask.IsCompleted)
                    return _refreshTask;
                _refreshTask = Task.Run((Func<Task>)RefreshCoreAsync);
                return _refreshTask;
            }
        }

        private async Task RefreshCoreAsync()
        {
            Process process = null;
            QuotaReading reading = null;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _codexExe,
                    Arguments = "app-server",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                process = new Process { StartInfo = startInfo };
                // Drain diagnostic output without retaining or displaying possibly sensitive data.
                process.ErrorDataReceived += delegate { };
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    if (!process.Start())
                        throw new QuotaException("无法启动 Codex 额度接口。");
                    _activeProcess = process;
                }
                process.BeginErrorReadLine();
                var timer = Stopwatch.StartNew();
                var serializer = CreateSerializer();
                await SendAsync(process, serializer, new
                {
                    method = "initialize",
                    id = 1,
                    @params = new
                    {
                        clientInfo = new
                        {
                            name = "codex_quota_taskbar",
                            title = "Codex Quota Taskbar",
                            version = "1.0.0"
                        }
                    }
                }).ConfigureAwait(false);
                await ReadReplyAsync(process, serializer, timer, 1).ConfigureAwait(false);
                await SendAsync(process, serializer, new { method = "initialized", @params = new { } }).ConfigureAwait(false);
                await SendAsync(process, serializer, new { method = "account/rateLimits/read", id = 2 }).ConfigureAwait(false);
                var result = await ReadReplyAsync(process, serializer, timer, 2).ConfigureAwait(false);
                reading = ParseReading(result, DateTimeOffset.UtcNow);
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    _lastGoodReading = CopyReading(reading);
                }
            }
            catch (QuotaException ex)
            {
                reading = CreateFailure(ex.Message);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                reading = CreateFailure("找不到或无法启动 Codex，请检查 Codex 安装。");
            }
            catch (Exception)
            {
                // Do not expose raw app-server errors, process arguments, or local credentials.
                reading = CreateFailure("读取额度失败，稍后会自动重试。");
            }
            finally
            {
                lock (_gate)
                {
                    if (Object.ReferenceEquals(_activeProcess, process))
                        _activeProcess = null;
                }
                StopOwnedProcess(process);
            }
            if (reading != null)
                Publish(reading);
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 64 };
        }

        private static async Task SendAsync(Process process, JavaScriptSerializer serializer, object message)
        {
            await process.StandardInput.WriteLineAsync(serializer.Serialize(message)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        private static async Task<IDictionary<string, object>> ReadReplyAsync(
            Process process, JavaScriptSerializer serializer, Stopwatch timer, int expectedId)
        {
            while (true)
            {
                int remaining = TimeoutMilliseconds - (int)timer.ElapsedMilliseconds;
                if (remaining <= 0)
                    throw new QuotaException("额度接口响应超时，稍后会自动重试。");
                Task<string> lineTask = process.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(lineTask, Task.Delay(remaining)).ConfigureAwait(false) != lineTask)
                    throw new QuotaException("额度接口响应超时，稍后会自动重试。");
                string line = await lineTask.ConfigureAwait(false);
                if (line == null)
                    throw new QuotaException("Codex 额度接口已退出，稍后会自动重试。");
                if (line.Length == 0)
                    continue;
                IDictionary<string, object> message;
                try
                {
                    message = serializer.DeserializeObject(line) as IDictionary<string, object>;
                }
                catch (ArgumentException)
                {
                    continue;
                }
                object id;
                if (message == null || !message.TryGetValue("id", out id) ||
                    Convert.ToString(id, CultureInfo.InvariantCulture) != expectedId.ToString(CultureInfo.InvariantCulture))
                    continue;
                object errorValue;
                if (message.TryGetValue("error", out errorValue) && errorValue != null)
                {
                    var error = errorValue as IDictionary<string, object>;
                    string errorMessage = ReadString(error, "message");
                    if (errorMessage != null)
                    {
                        string lower = errorMessage.ToLowerInvariant();
                        if (lower.Contains("login") || lower.Contains("logged in") || lower.Contains("authenticated") ||
                            lower.Contains("authentication") || lower.Contains("unauthorized"))
                            throw new QuotaException("请先在 Codex 中登录 ChatGPT 账户。");
                    }
                    throw new QuotaException("Codex 额度接口暂不可用，稍后会自动重试。");
                }
                object result;
                if (!message.TryGetValue("result", out result) || !(result is IDictionary<string, object>))
                    throw new QuotaException("Codex 返回了无法识别的额度数据。");
                return (IDictionary<string, object>)result;
            }
        }

        // Internal so a small fixture runner can verify protocol variants without a model call.
        internal static QuotaReading ParseReading(IDictionary<string, object> result, DateTimeOffset fetchedAt)
        {
            QuotaReading selected = null;
            object mapValue;
            if (result != null && result.TryGetValue("rateLimitsByLimitId", out mapValue))
            {
                var map = mapValue as IDictionary<string, object>;
                if (map != null)
                {
                    // Prefer the overall Codex quota over model-specific or other metered buckets.
                    object codexValue;
                    if (map.TryGetValue("codex", out codexValue))
                        selected = ReadWeeklyBucket(codexValue as IDictionary<string, object>, "codex", fetchedAt);
                    if (selected == null)
                    {
                        var keys = new List<string>(map.Keys);
                        keys.Sort(StringComparer.Ordinal);
                        foreach (string key in keys)
                        {
                            var bucket = map[key] as IDictionary<string, object>;
                            if (String.Equals(ReadString(bucket, "limitId"), "codex", StringComparison.OrdinalIgnoreCase))
                            {
                                selected = ReadWeeklyBucket(bucket, key, fetchedAt);
                                if (selected != null)
                                    break;
                            }
                        }
                        if (selected == null)
                        {
                            foreach (string key in keys)
                            {
                                selected = ReadWeeklyBucket(map[key] as IDictionary<string, object>, key, fetchedAt);
                                if (selected != null)
                                    break;
                            }
                        }
                    }
                }
            }
            if (selected == null && result != null)
            {
                object legacy;
                if (result.TryGetValue("rateLimits", out legacy))
                    selected = ReadWeeklyBucket(legacy as IDictionary<string, object>, "codex", fetchedAt);
            }
            if (selected == null)
                throw new QuotaException("账户未返回周额度；请确认已登录 ChatGPT 账户。");
            return selected;
        }

        private static QuotaReading ReadWeeklyBucket(IDictionary<string, object> bucket, string fallbackId, DateTimeOffset fetchedAt)
        {
            if (bucket == null)
                return null;
            foreach (string windowName in new[] { "primary", "secondary" })
            {
                object windowValue;
                if (!bucket.TryGetValue(windowName, out windowValue))
                    continue;
                var window = windowValue as IDictionary<string, object>;
                double duration;
                double used;
                if (!ReadNumber(window, "windowDurationMins", out duration) || duration != 10080 ||
                    !ReadNumber(window, "usedPercent", out used))
                    continue;
                DateTimeOffset? reset = null;
                double unixSeconds;
                if (ReadNumber(window, "resetsAt", out unixSeconds))
                {
                    try { reset = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(unixSeconds); }
                    catch (ArgumentOutOfRangeException) { }
                }
                return new QuotaReading
                {
                    UsedPercent = used,
                    RemainingPercent = Math.Max(0, Math.Min(100, 100 - used)),
                    ResetsAt = reset,
                    LimitId = ReadString(bucket, "limitId") ?? fallbackId,
                    PlanType = ReadString(bucket, "planType"),
                    FetchedAt = fetchedAt,
                    Error = null,
                    IsStale = false
                };
            }
            return null;
        }

        private static string ReadString(IDictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) ? value as string : null;
        }

        private static bool ReadNumber(IDictionary<string, object> data, string key, out double number)
        {
            number = 0;
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null || value is bool)
                return false;
            return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number);
        }

        private QuotaReading CreateFailure(string message)
        {
            lock (_gate)
            {
                var reading = _lastGoodReading == null
                    ? new QuotaReading { RemainingPercent = Double.NaN, UsedPercent = Double.NaN }
                    : CopyReading(_lastGoodReading);
                reading.IsStale = true;
                reading.Error = message;
                return reading;
            }
        }

        private static QuotaReading CopyReading(QuotaReading reading)
        {
            return new QuotaReading
            {
                RemainingPercent = reading.RemainingPercent,
                UsedPercent = reading.UsedPercent,
                ResetsAt = reading.ResetsAt,
                LimitId = reading.LimitId,
                PlanType = reading.PlanType,
                FetchedAt = reading.FetchedAt,
                Error = reading.Error,
                IsStale = reading.IsStale
            };
        }

        private void Publish(QuotaReading reading)
        {
            Action<QuotaReading> handlers;
            lock (_gate)
            {
                if (_disposed)
                    return;
                handlers = Updated;
            }
            if (handlers == null)
                return;
            foreach (Action<QuotaReading> handler in handlers.GetInvocationList())
            {
                try { handler(CopyReading(reading)); }
                catch (Exception) { }
            }
        }

        private static void StopOwnedProcess(Process process)
        {
            if (process == null)
                return;
            try { process.StandardInput.Close(); }
            catch (Exception) { }
            try
            {
                if (!process.WaitForExit(750))
                {
                    process.Kill();
                    process.WaitForExit(1500);
                }
            }
            catch (Exception) { }
            try { process.Dispose(); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            Process process;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                process = _activeProcess;
                _activeProcess = null;
                Updated = null;
            }
            StopOwnedProcess(process);
        }

        private sealed class QuotaException : Exception
        {
            public QuotaException(string message) : base(message) { }
        }
    }
}
