using LiveCaptionsTranslator.Core.Asr;
using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Doubao;

namespace LiveCaptionsTranslator.Integration.Tests;

/// <summary>
/// Talks to the real Doubao endpoint with the committed speech sample.
/// <para>
/// The device is registered from scratch here (the registration is pure C#, no Python and no
/// committed credential), then the sample is captured, Opus encoded, streamed and the transcript
/// asserted. This is the only test that proves the whole hand-rolled chain works.
/// </para>
/// </summary>
[Trait("Category", "Secondary")]
public class DoubaoAsrIntegrationTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static WavAudio ReadFixture() =>
        WavFile.Read(FixturePath("iflytek-zh-cn-16k.wav"));

    /// <summary>Wraps the real transport and records what the server actually answered.</summary>
    private sealed class LoggingTransport : IDoubaoTransport
    {
        private readonly IDoubaoTransport inner;
        private readonly List<string> log;

        public LoggingTransport(IDoubaoTransport inner, List<string> log)
        {
            this.inner = inner;
            this.log = log;
        }

        public bool IsOpen => inner.IsOpen;

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            log.Add($"connect {uri.Host}{uri.PathAndQuery}");
            return inner.ConnectAsync(uri, headers, cancellationToken);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            sentCount++;

            // Log the first few packets verbatim (the JSON payload is readable inside the frame),
            // then every 20th by size only, so the log stays readable.
            if (sentCount <= 3)
            {
                string text = System.Text.Encoding.UTF8.GetString(payload.Span);
                string printable = new(text.Select(c => char.IsControl(c) || c == '�' ? '.' : c).ToArray());
                log.Add($"sent #{sentCount} bytes={payload.Length} :: {printable}");
            }
            else if (sentCount % 20 == 0)
            {
                log.Add($"sent #{sentCount} bytes={payload.Length}");
            }

            await inner.SendAsync(payload, cancellationToken);
        }

        private int sentCount;

        public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
        {
            byte[]? data = await inner.ReceiveAsync(cancellationToken);

            if (data is null)
            {
                log.Add("closed by server");
                return null;
            }

            try
            {
                var response = DoubaoAsrResponse.Parse(data);
                log.Add($"recv {response.MessageType} status={response.StatusCode} " +
                        $"json={(string.IsNullOrEmpty(response.ResultJson) ? "-" : response.ResultJson)}");
            }
            catch (Exception exception)
            {
                log.Add($"recv <unparsed> bytes={data.Length} ({exception.GetType().Name})");
            }

            return data;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public void The_committed_sample_is_the_expected_format()
    {
        var audio = ReadFixture();

        Assert.Equal(AudioFormat.SampleRate, audio.SampleRate);
        Assert.Equal(AudioFormat.Channels, audio.Channels);
        Assert.Equal(AudioFormat.BitsPerSample, audio.BitsPerSample);
    }

    [Fact]
    public async Task Registration_produces_a_usable_device_pair()
    {
        var credentials = await new DoubaoDeviceRegistrar().RegisterAsync();

        Assert.True(credentials.IsComplete, "registration did not return a device_id/app_key pair");
        Assert.False(string.IsNullOrWhiteSpace(credentials.DeviceId));
        Assert.False(string.IsNullOrWhiteSpace(credentials.AppKey));

        // The device id is a decimal string, the app key a short opaque token.
        Assert.All(credentials.DeviceId, character => Assert.True(char.IsDigit(character)));
        Assert.InRange(credentials.AppKey.Length, 4, 64);
    }

    [Fact]
    public async Task The_real_endpoint_recognises_the_committed_chinese_sample()
    {
        var credentials = await new DoubaoDeviceRegistrar().RegisterAsync();
        Assert.True(credentials.IsComplete, "could not register a device; the endpoint was not exercised");

        var audio = ReadFixture();

        // The endpoint intermittently rejects a freshly opened session (SessionFailed 50700000),
        // which is a service-side transient rather than a defect here. Retry the session a couple
        // of times with the same device before giving up.
        string transcript = string.Empty;
        string wire = string.Empty;

        const int attempts = 5;

        for (int attempt = 1; attempt <= attempts && string.IsNullOrWhiteSpace(transcript); attempt++)
        {
            (transcript, wire) = await TryRecogniseAsync(credentials, audio);

            // The endpoint throttles bursts of sessions; give it room between attempts.
            if (string.IsNullOrWhiteSpace(transcript) && attempt < attempts)
                await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Assert.False(
            string.IsNullOrWhiteSpace(transcript),
            $"the service returned no transcript after {attempts} attempts." + Environment.NewLine + wire);

        // Exact punctuation is the service's business; the words are what matter.
        Assert.Contains("今天天气", transcript);
    }

    /// <summary>One full attempt: capture the sample, stream it, and collect the transcript.</summary>
    private static async Task<(string Transcript, string Wire)> TryRecogniseAsync(
        DoubaoCredentials credentials,
        WavAudio audio)
    {
        var source = new WavAudioSource(audio.Pcm);
        var sink = new CollectingSink();
        var wireLog = new List<string>();

        await using var provider = new DoubaoAsrProvider(
            credentials,
            transportFactory: () => new LoggingTransport(new WebSocketDoubaoTransport(), wireLog),
            streamOptions: new DoubaoStreamOptions
            {
                MaxConsecutiveFailures = 1,
                ReconnectDelay = TimeSpan.FromMilliseconds(200),
            });

        await provider.StartAsync(new AsrContext
        {
            Sink = sink,
            Options = new AsrOptions(),
            AudioSource = source,
        });

        await source.EmitAllAsync();
        await provider.StopAsync();

        return (
            sink.Transcript,
            "notices: " + string.Join(" | ", sink.Notices) + Environment.NewLine +
            "wire:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", wireLog));
    }
}
