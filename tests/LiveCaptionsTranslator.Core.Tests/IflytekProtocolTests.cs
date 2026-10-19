using LiveCaptionsTranslator.Core.Audio;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Core.Tests;

public class IflytekProtocolTests
{
    private static readonly IflytekCredentials Credentials = new()
    {
        ApiKey = Golden.String("apiKey"),
        ApiSecret = Golden.String("apiSecret"),
        Host = Golden.String("host"),
        Path = Golden.String("path"),
        AppId = Golden.String("appId"),
    };

    [Fact]
    public void Frames_match_the_python_reference_byte_for_byte()
    {
        int checked_ = 0;

        foreach (var vector in Golden.Items("frames"))
        {
            int status = vector.GetProperty("status").GetInt32();
            byte[] audio = Convert.FromBase64String(vector.GetProperty("audioBase64").GetString()!);
            string expected = vector.GetProperty("json").GetString()!;

            IflytekBusiness? business = vector.TryGetProperty("business", out var businessElement)
                ? new IflytekBusiness
                {
                    Language = businessElement.GetProperty("language").GetString(),
                    Domain = businessElement.GetProperty("domain").GetString(),
                    Accent = businessElement.GetProperty("accent").GetString(),
                    VadEos = businessElement.GetProperty("vad_eos").GetInt32(),
                    Dwa = businessElement.TryGetProperty("dwa", out var dwa) ? dwa.GetString() : null,
                    Nunum = businessElement.TryGetProperty("nunum", out var nunum) ? nunum.GetInt32() : null,
                    Ptt = businessElement.TryGetProperty("ptt", out var ptt) ? ptt.GetInt32() : null,
                }
                : null;

            Assert.Equal(expected, IflytekProtocol.BuildFrame(status, audio, Credentials, business));
            checked_++;
        }

        Assert.Equal(5, checked_);
    }

    [Fact]
    public void Default_business_matches_the_reference_client()
    {
        var expected = Golden.Root.GetProperty("defaultBusiness");

        var business = IflytekBusiness.Default;

        Assert.Equal(expected.GetProperty("language").GetString(), business.Language);
        Assert.Equal(expected.GetProperty("domain").GetString(), business.Domain);
        Assert.Equal(expected.GetProperty("accent").GetString(), business.Accent);
        Assert.Equal(expected.GetProperty("vad_eos").GetInt32(), business.VadEos);
        Assert.Equal(expected.GetProperty("dwa").GetString(), business.Dwa);
        Assert.Equal(expected.GetProperty("nunum").GetInt32(), business.Nunum);
        Assert.Equal(expected.GetProperty("ptt").GetInt32(), business.Ptt);
    }

    [Fact]
    public void Null_business_fields_are_omitted()
    {
        string frame = IflytekProtocol.BuildFrame(
            0,
            ReadOnlySpan<byte>.Empty,
            Credentials,
            new IflytekBusiness { Language = "ja", Accent = null, VadEos = null, Nunum = null, Ptt = null });

        Assert.Contains("\"business\":{\"language\":\"ja\",\"domain\":\"iat\",\"dwa\":\"wpgs\"}", frame);
        Assert.DoesNotContain("accent", frame);
        Assert.DoesNotContain("vad_eos", frame);
        Assert.DoesNotContain("nunum", frame);
        Assert.DoesNotContain("ptt", frame);
    }

    [Fact]
    public void Chunking_matches_the_python_reference_byte_for_byte()
    {
        byte[] pcm = Golden.Bytes("chunkCase.pcmBase64");
        int chunkBytes = Golden.Root.GetProperty("chunkCase").GetProperty("chunkBytes").GetInt32();
        var expected = Golden.Root.GetProperty("chunkCase").GetProperty("chunksBase64")
            .EnumerateArray().Select(item => item.GetString()!).ToList();

        var chunks = IflytekProtocol.ChunkPcm(pcm, chunkBytes);

        Assert.Equal(expected.Count, chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(expected[i], Convert.ToBase64String(chunks[i]));
            Assert.Equal(chunkBytes, chunks[i].Length);
        }
    }

    [Fact]
    public void Chunking_pads_the_last_piece_with_zeroes()
    {
        var chunks = IflytekProtocol.ChunkPcm(new byte[] { 1, 2, 3 }, 4);

        Assert.Single(chunks);
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, chunks[0]);
    }

    [Fact]
    public void Chunking_an_empty_buffer_yields_nothing()
    {
        Assert.Empty(IflytekProtocol.ChunkPcm(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Leading_silence_matches_the_python_reference()
    {
        byte[] silence = IflytekProtocol.LeadingSilence();

        Assert.Equal(Golden.Bytes("leadingSilenceBase64"), silence);
        Assert.Equal(200, Golden.Int("leadingSilenceMs"));
        Assert.Equal(200, IflytekProtocol.LeadingSilenceMs);
    }

    [Fact]
    public void Protocol_constants_match_the_reference_client()
    {
        Assert.Equal(AudioFormat.SampleRate, Golden.Int("sampleRate"));
        Assert.Equal(AudioFormat.ChunkBytes, Golden.Int("chunkBytes"));
        Assert.Equal(AudioFormat.ChunkMs, Golden.Int("chunkMs"));
    }
}
