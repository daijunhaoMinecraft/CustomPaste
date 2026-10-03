using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using CustomPaste.Models;
using CustomPaste.Services;

internal static class StreamingTests
{
    public static void Run(Action<string, Action> run, Action<string, Func<Task>> runAsync)
    {
        run("Settings notify once per actual change", () =>
        {
            var settings = new AppSettings(); var count = 0;
            settings.PropertyChanged += (_, _) => count++;
            settings.Theme = "Dark"; settings.Theme = "Dark"; settings.AIStreamEnabled = true;
            Check(count == 2); Check(settings.Clone().AIStreamEnabled);
        });
        run("Autosave debounces, defers busy work, flushes and cancels", () =>
        {
            var saved = 0; var idle = true;
            using var autosave = new SettingsAutoSave(Dispatcher.CurrentDispatcher, () => idle, () => saved++, 25);
            autosave.Request(); autosave.Request(); autosave.Request(); Pump(100);
            Check(saved == 1 && !autosave.Pending);
            idle = false; autosave.Request(); Pump(100); Check(saved == 1 && autosave.Pending);
            idle = true; autosave.ResumeIfPending(); Pump(100); Check(saved == 2 && !autosave.Pending);
            autosave.Request(); autosave.Flush(); Check(saved == 3 && !autosave.Pending);
            autosave.Request(); autosave.Cancel(); Pump(100); Check(saved == 3);
        });
        run("Incremental input joins split CRLF and surrogate pair", () =>
        {
            var buffer = new StreamingTextBuffer();
            Check(buffer.Push("hello\r") == "hello"); Check(buffer.Push("\nworld") == "\nworld");
            Check(buffer.Push("\ud83c") == ""); Check(buffer.Push("\udf0f") == "🌏");
            Check(buffer.Push("\r") == ""); Check(buffer.Push("", true) == "\n");
        });
        runAsync("SSE yields first token before the server completes", async () =>
        {
            using var gated = new GatedStream(Chunk("你"), Chunk("好") + End);
            using var client = Client(gated, request =>
            {
                using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(json.RootElement.GetProperty("stream").GetBoolean());
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var iterator = new TranslationService(client).StreamTranslateAsync("Hello", Config(), cts.Token).GetAsyncEnumerator();
            try
            {
                Check(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
                Check(iterator.Current == "你" && !gated.Release.Task.IsCompleted);
                gated.Release.SetResult();
                Check(await iterator.MoveNextAsync()); Check(iterator.Current == "好");
                Check(!await iterator.MoveNextAsync());
            }
            finally { gated.Release.TrySetResult(); }
        });
        runAsync("SSE handles UTF8 split across byte boundaries and CRLF events", async () =>
        {
            using var content = new FragmentedStream(": ping\r\nevent: message\r\n" + Chunk("中文🌏\n").Replace("\n\n", "\r\n\r\n") + End);
            using var client = Client(content);
            Check(await Collect(new TranslationService(client), Config()) == "中文🌏\n");
        });
        runAsync("SSE ignores role, reasoning and usage-only chunks", async () =>
        {
            var text = "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"private thought\"},\"finish_reason\":null}]}\n\n" +
                Chunk("译文") + Stop + "data: {\"choices\":[],\"usage\":{}}\n\ndata: [DONE]\n\n";
            using var client = Client(new FragmentedStream(text));
            Check(await Collect(new TranslationService(client), Config()) == "译文");
        });
        foreach (var tail in new[] {
            "", "data: [DONE]\n\n", Stop,
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"refusal\":\"private-response\"},\"finish_reason\":null}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{}]},\"finish_reason\":null}]}\n\n",
            "data: {\"error\":{\"message\":\"private-response\"}}\n\n", "data: malformed\n\n" })
            runAsync("SSE stops on incomplete or invalid stream without exposing response", async () =>
            {
                using var client = Client(new FragmentedStream(Chunk("partial") + tail));
                var error = await Fails<TranslationException>(() => Collect(new TranslationService(client), Config()));
                Check(!error.Message.Contains("private-response"));
            });
        runAsync("SSE output limit is enforced before oversized delta is emitted", async () =>
        {
            using var client = new HttpClient(new Handler(_ => Response(new MemoryStream(Encoding.UTF8.GetBytes(Chunk("ab") + Chunk("cdef") + End)))));
            var config = Config(); config.MaxTextLength = 3; var received = "";
            await Fails<TranslationException>(async () => { await foreach (var text in new TranslationService(client).StreamTranslateAsync("Hi", config)) received += text; });
            Check(received == "ab");
        });
        runAsync("SSE cancellation interrupts a pending network read", async () =>
        {
            using var gated = new GatedStream(Chunk("first"), End);
            using var client = Client(gated);
            using var cts = new CancellationTokenSource();
            await using var iterator = new TranslationService(client).StreamTranslateAsync("Hello", Config(), cts.Token).GetAsyncEnumerator();
            Check(await iterator.MoveNextAsync()); cts.Cancel();
            await Fails<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
        });
        runAsync("SSE rejects non-stream responses instead of silently buffering", async () =>
        {
            using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }));
            await Fails<TranslationException>(() => Collect(new TranslationService(client), Config()));
        });
        foreach (var fail in new[] { false, true })
            runAsync(fail ? "Realtime partial failure is not saved as completed history" : "Realtime paste sends each fragment once at zero artificial delay", async () =>
            {
                var path = Path.Combine(Path.GetTempPath(), "CustomPaste.StreamTests-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var storage = new StorageService(path); var history = new HistoryService(storage);
                    await using var log = new LogService(path, Dispatcher.CurrentDispatcher);
                    var platform = new Platform();
                    using var client = Client(new FragmentedStream(Chunk("one\r") + Chunk("\ntwo") + (fail ? "" : End)));
                    var paste = new PasteService(new TranslationService(client), history, log, platform);
                    var config = Config(); config.TranslateOnPaste = true; config.StreamOnPaste = false;
                    await paste.ExecuteAsync(PasteAction.Custom, config);
                    Check(platform.Output.ToString() == "one\ntwo"); Check(platform.Delays.All(x => x == 0));
                    Check(history.Entries.Count == (fail ? 0 : 1)); Check(!paste.IsBusy);
                }
                finally
                {
                    var full = Path.GetFullPath(path);
                    if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("CustomPaste.StreamTests-")) throw new Exception("Unsafe cleanup");
                    if (Directory.Exists(full)) Directory.Delete(full, true);
                }
            });
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static async Task<T> Fails<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T e) { return e; } throw new Exception("Expected " + typeof(T).Name); }
    private static AppSettings Config() => new()
    {
        Provider = TranslationProvider.AI,
        AIModel = "test-model",
        AIStreamEnabled = true,
        ProtectedAIEndpoint = SecretProtector.Protect("http://localhost:1234/v1")
    };
    private const string Stop = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
    private const string End = Stop + "data: [DONE]\n\n";
    private static string Chunk(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } } }) + "\n\n";
    private static HttpResponseMessage Response(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream"); return response;
    }
    private static HttpClient Client(Stream stream, Action<HttpRequestMessage>? inspect = null) => new(new Handler(request => { inspect?.Invoke(request); return Response(stream); }));
    private static async Task<string> Collect(TranslationService service, AppSettings settings)
    { var result = new StringBuilder(); await foreach (var fragment in service.StreamTranslateAsync("Hi", settings)) result.Append(fragment); return result.ToString(); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request)); }
    private sealed class FragmentedStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], token); }
    private sealed class GatedStream(string first, string rest) : Stream
    {
        private readonly MemoryStream _first = new(Encoding.UTF8.GetBytes(first));
        private readonly MemoryStream _rest = new(Encoding.UTF8.GetBytes(rest));
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_first.Position < _first.Length) return await _first.ReadAsync(buffer, token);
            await Release.Task.WaitAsync(token); return await _rest.ReadAsync(buffer, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) { _first.Dispose(); _rest.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    private sealed class Platform : IPastePlatform
    {
        public StringBuilder Output { get; } = new(); public List<int> Delays { get; } = new();
        public IntPtr GetTarget() => new(1);
        public IDisposable WatchCancellation(Action cancel) => new Nothing();
        public Task<string> ReadTextAsync(CancellationToken token) => Task.FromResult("Hello");
        public Task WaitForReleaseAsync(IntPtr target, CancellationToken token) => Task.CompletedTask;
        public Task TypeTextAsync(string text, IntPtr target, int delay, CancellationToken token) { Output.Append(text); Delays.Add(delay); return Task.CompletedTask; }
        public Task PasteClipboardAsync(string text, IntPtr target, AppSettings settings, CancellationToken token) => throw new Exception("Realtime must not replace clipboard");
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}
