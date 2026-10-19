using System.Diagnostics;
using System.Text.Json;

using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Core.Tests;

public class IflytekIatProviderTests
{
    private static readonly string PartialJson =
        """{"code":0,"data":{"status":1,"result":{"sn":1,"ls":false,"pgs":"apd","ws":[{"cw":[{"w":"今天"}]}]}}}""";

    private static readonly string RevisionJson =
        """{"code":0,"data":{"status":1,"result":{"sn":2,"ls":false,"pgs":"rpl","rg":[1,1],"ws":[{"cw":[{"w":"今天天气"}]}]}}}""";

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<FakeTransport> transports = [];

        public Harness(TimeSpan? reconnectDelay = null, TimeSpan? gracefulStop = null, bool pad = false)
        {
            Options = new IflytekIatOptions
            {
                PadWithLeadingSilence = pad,
                ReconnectDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(50),
                GracefulStopTimeout = gracefulStop ?? TimeSpan.FromMilliseconds(300),
                ReceiveTimeout = TimeSpan.FromSeconds(5),
            };

            Provider = new IflytekIatProvider(Options, () =>
            {
                var transport = new FakeTransport();
                lock (transports)
                    transports.Add(transport);

                return transport;
            });
        }

        public IflytekIatOptions Options { get; }

        public IflytekIatProvider Provider { get; }

        public RecordingSink Sink { get; } = new();

        public FakeAudioSource Audio { get; } = new();

        public FakeTransport Transport(int index = 0)
        {
            lock (transports)
                return transports[index];
        }

        /// <summary>Index-safe accessor, for use inside <see cref="WaitUntil"/> conditions.</summary>
        public int SentCount(int index = 0)
        {
            lock (transports)
                return index < transports.Count ? transports[index].Sent.Count : 0;
        }

        public int TransportCount
        {
            get
            {
                lock (transports)
                    return transports.Count;
            }
        }

        public Task StartAsync() => Provider.StartAsync(new AsrContext
        {
            Sink = Sink,
            Options = new AsrOptions(),
            AudioSource = Audio,
        });

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 8000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("condition was not met in time");

            await Task.Delay(20);
        }
    }

    private static int StatusOf(string frame) =>
        JsonDocument.Parse(frame).RootElement.GetProperty("data").GetProperty("status").GetInt32();

    private static byte[] AudioOf(string frame) =>
        Convert.FromBase64String(
            JsonDocument.Parse(frame).RootElement.GetProperty("data").GetProperty("audio").GetString()!);

    [Fact]
    public void Provider_reports_the_contract_the_host_relies_on()
    {
        // Through the interface: LanguageIsHostControlled is a default member.
        IAsrProvider provider = new IflytekIatProvider();

        Assert.Equal("iflytek-iat", provider.Id);
        Assert.True(provider.RequiresAudioSource);
        Assert.NotEmpty(provider.SupportedLanguages);

        // The host picks the language here, unlike Windows Live Captions.
        Assert.True(provider.LanguageIsHostControlled);
    }

    [Fact]
    public void Only_the_language_covered_by_the_integration_sample_is_marked_verified()
    {
        var provider = new IflytekIatProvider();

        var verified = provider.SupportedLanguages.Where(language => language.Verified).ToList();

        Assert.Single(verified);
        Assert.Equal("zh_cn", verified[0].Id);
        Assert.Equal("mandarin", verified[0].Accent);

        // Everything else is merely accepted by the endpoint; the README says the same.
        Assert.All(
            provider.SupportedLanguages.Where(language => !language.Verified),
            language => Assert.NotEqual("mandarin", language.Accent));
    }

    [Fact]
    public async Task Starting_without_an_audio_source_is_rejected()
    {
        await using var provider = new IflytekIatProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StartAsync(new AsrContext
        {
            Sink = new RecordingSink(),
            Options = new AsrOptions(),
        }));
    }

    [Fact]
    public async Task Audio_is_streamed_as_40ms_frames_with_a_signed_url()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        harness.Audio.EmitChunk();
        await WaitUntil(() => harness.SentCount() >= 1);

        var transport = harness.Transport();
        Assert.NotNull(transport.Url);
        Assert.StartsWith("wss://100ime-iat-api.xfyun.cn/v2/iat?", transport.Url);
        Assert.Contains("authorization=", transport.Url);

        string first = transport.Sent[0];
        Assert.Equal(0, StatusOf(first));
        Assert.Equal(AudioFormat.ChunkBytes, AudioOf(first).Length);

        var firstFrame = JsonDocument.Parse(first).RootElement;
        Assert.Equal("100IME", firstFrame.GetProperty("common").GetProperty("app_id").GetString());
        Assert.Equal("zh_cn", firstFrame.GetProperty("business").GetProperty("language").GetString());

        harness.Audio.EmitChunk();
        await WaitUntil(() => transport.Sent.Count >= 2);

        string second = transport.Sent[1];
        Assert.Equal(1, StatusOf(second));
        Assert.False(JsonDocument.Parse(second).RootElement.TryGetProperty("business", out _));

        await harness.Provider.StopAsync();
    }

    [Fact]
    public async Task Leading_silence_is_sent_first_when_enabled()
    {
        await using var harness = new Harness(pad: true);
        await harness.StartAsync();

        // 200 ms of silence at 16 kHz mono PCM16 is 6400 bytes, i.e. five 40 ms frames.
        await WaitUntil(() => harness.SentCount() >= 5);

        var sent = harness.Transport().Sent.Take(5).ToList();

        Assert.Equal(0, StatusOf(sent[0]));
        Assert.Equal(1, StatusOf(sent[1]));
        Assert.Equal(IflytekProtocol.LeadingSilence().Length, sent.Sum(frame => AudioOf(frame).Length));
        Assert.All(sent, frame => Assert.All(AudioOf(frame), value => Assert.Equal(0, value)));

        await harness.Provider.StopAsync();
    }

    [Fact]
    public async Task Recognised_text_is_published_to_the_sink()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        harness.Audio.EmitChunk();
        await WaitUntil(() => harness.SentCount() >= 1);

        harness.Transport().Enqueue(PartialJson);
        await WaitUntil(() => harness.Sink.Partials.Count >= 1);
        Assert.Equal("今天", harness.Sink.Partials[^1]);

        // A replacement revision must rewrite the transcript, not append to it.
        harness.Transport().Enqueue(RevisionJson);
        await WaitUntil(() => harness.Sink.Partials.Count >= 2);
        Assert.Equal("今天天气", harness.Sink.Partials[^1]);

        await harness.Provider.StopAsync();
    }

    [Fact]
    public async Task A_dropped_connection_is_reported_and_reconnected()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        harness.Audio.EmitChunk();
        await WaitUntil(() => harness.SentCount(0) >= 1);

        harness.Transport(0).Drop();

        await WaitUntil(() => harness.TransportCount >= 2);
        Assert.True(harness.Sink.Notices.Any(notice => notice.Contains("重连")),
            "expected a reconnect notice, saw: " + string.Join(" | ", harness.Sink.Notices));

        // The new session starts from scratch but must still receive audio.
        harness.Audio.EmitChunk();
        await WaitUntil(() => harness.SentCount(1) >= 1);
        Assert.Equal(0, StatusOf(harness.Transport(1).Sent[0]));

        await harness.Provider.StopAsync();
    }

    [Fact]
    public async Task Text_recognised_before_a_drop_is_kept_after_reconnecting()
    {
        await using var harness = new Harness();
        await harness.StartAsync();

        harness.Audio.EmitChunk();
        await WaitUntil(() => harness.SentCount(0) >= 1);

        harness.Transport(0).Enqueue(PartialJson);
        await WaitUntil(() => harness.Sink.Partials.Count >= 1);

        harness.Transport(0).Drop();
        await WaitUntil(() => harness.TransportCount >= 2);

        harness.Audio.EmitChunk();
        await WaitUntil(() => harness.SentCount(1) >= 1);

        harness.Transport(1).Enqueue(PartialJson);
        await WaitUntil(() => harness.Sink.Partials.Count >= 2);

        // "今天" from the first session plus "今天" from the second, not a restart.
        Assert.Equal("今天今天", harness.Sink.Partials[^1]);

        await harness.Provider.StopAsync();
    }

    [Fact]
    public async Task Reconnect_gives_up_after_the_configured_number_of_attempts()
    {
        var options = new IflytekIatOptions
        {
            PadWithLeadingSilence = false,
            ReconnectDelay = TimeSpan.FromMilliseconds(10),
            GracefulStopTimeout = TimeSpan.FromMilliseconds(200),
            MaxReconnectAttempts = 2,
            ReceiveTimeout = TimeSpan.FromSeconds(5),
        };

        int opened = 0;

        // Every connection is dropped by the server as soon as it is opened.
        await using var provider = new IflytekIatProvider(options, () =>
        {
            var transport = new FakeTransport();
            Interlocked.Increment(ref opened);
            transport.Drop();
            return transport;
        });

        var sink = new RecordingSink();

        await provider.StartAsync(new AsrContext
        {
            Sink = sink,
            Options = new AsrOptions(),
            AudioSource = new FakeAudioSource(),
        });

        await WaitUntil(() => sink.Errors.Count >= 1);

        Assert.Contains("重连", sink.Errors[0].Message);
        Assert.True(opened >= 3, $"expected at least 3 sessions, saw {opened}");

        await provider.StopAsync();
    }

    [Fact]
    public async Task Stopping_flushes_a_final_frame()
    {
        await using var harness = new Harness(gracefulStop: TimeSpan.FromMilliseconds(300));
        await harness.StartAsync();

        harness.Audio.EmitChunk();
        await WaitUntil(() => harness.SentCount() >= 1);

        await harness.Provider.StopAsync();

        var transport = harness.Transport();
        Assert.Equal(2, StatusOf(transport.Sent[^1]));
        Assert.Empty(AudioOf(transport.Sent[^1]));

        // A graceful stop must not look like a dropped connection.
        Assert.DoesNotContain(harness.Sink.Notices, notice => notice.Contains("重连"));
    }
}
