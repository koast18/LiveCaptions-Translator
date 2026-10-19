using System.Text.Json;
using LiveCaptionsTranslator.Core.Iflytek;

namespace LiveCaptionsTranslator.Core.Tests;

public class IflytekTranscriptTests
{
    [Fact]
    public void Every_scenario_matches_the_python_reference()
    {
        int scenarios = 0;
        int steps = 0;

        foreach (var scenario in Golden.Items("scenarios"))
        {
            var transcript = new IflytekTranscript();

            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                var result = IflytekResultParser.ParseResult(step.GetProperty("result"));
                string text = transcript.Feed(result);

                Assert.Equal(step.GetProperty("text").GetString(), text);
                Assert.Equal(step.GetProperty("lastSn").GetInt32(), transcript.LastSn);
                Assert.Equal(step.GetProperty("isLast").GetBoolean(), transcript.IsLast);

                steps++;
            }

            scenarios++;
        }

        Assert.Equal(6, scenarios);
        Assert.True(steps >= 15, $"expected at least 15 steps, saw {steps}");
    }

    [Fact]
    public void Replacement_drops_the_slots_inside_rg()
    {
        var transcript = new IflytekTranscript();

        transcript.Feed(new IflytekWordResult(1, "apd", null, false, "今天"));
        transcript.Feed(new IflytekWordResult(2, "apd", null, false, "天气"));
        Assert.Equal(2, transcript.PartCount);

        string text = transcript.Feed(new IflytekWordResult(3, "rpl", new[] { 1, 2 }, false, "今天天气不错"));

        Assert.Equal("今天天气不错", text);
        Assert.Equal(1, transcript.PartCount);
    }

    [Fact]
    public void Slots_outside_rg_survive_a_replacement()
    {
        var transcript = new IflytekTranscript();

        transcript.Feed(new IflytekWordResult(1, "apd", null, false, "A"));
        transcript.Feed(new IflytekWordResult(2, "apd", null, false, "B"));
        transcript.Feed(new IflytekWordResult(3, "apd", null, false, "C"));

        string text = transcript.Feed(new IflytekWordResult(4, "rpl", new[] { 2, 3 }, false, "X"));

        Assert.Equal("AX", text);
    }

    [Fact]
    public void Empty_text_does_not_clear_an_existing_slot()
    {
        var transcript = new IflytekTranscript();

        transcript.Feed(new IflytekWordResult(1, "apd", null, false, "第一句"));
        transcript.Feed(new IflytekWordResult(1, "apd", null, false, string.Empty));

        Assert.Equal("第一句", transcript.Text);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var transcript = new IflytekTranscript();
        transcript.Feed(new IflytekWordResult(1, "apd", null, true, "你好"));

        transcript.Reset();

        Assert.Equal(string.Empty, transcript.Text);
        Assert.Equal(0, transcript.LastSn);
        Assert.False(transcript.IsLast);
        Assert.Equal(0, transcript.PartCount);
    }

    [Fact]
    public void Parser_reads_code_status_and_result()
    {
        const string json = """
            {"code":0,"message":"success","sid":"iat000e0a17@gz1",
             "data":{"status":1,"result":{"sn":1,"ls":false,"bg":0,"ed":0,"pgs":"apd","rst":"rlt",
                     "ws":[{"bg":0,"cw":[{"sc":0,"w":"今天"}],"ver":"1.3.2.9.6.52\r"}]}}}
            """;

        var message = IflytekResultParser.ParseMessage(json);

        Assert.False(message.IsError);
        Assert.False(message.IsFinal);
        Assert.Equal(1, message.Status);
        Assert.Equal("success", message.Message);
        Assert.NotNull(message.Result);
        Assert.Equal(1, message.Result.Sn);
        Assert.Equal("apd", message.Result.Pgs);
        Assert.Equal("今天", message.Result.Text);
    }

    [Fact]
    public void Parser_accepts_both_ws_shapes()
    {
        const string flat = """{"sn":1,"ws":[{"cw":[{"w":"甲"},{"w":"乙"}]}]}""";
        const string nested = """{"sn":1,"ws":[[{"cw":[{"w":"甲"}]},{"cw":[{"w":"乙"}]}]]}""";

        Assert.Equal("甲乙", IflytekResultParser.ParseResult(JsonDocument.Parse(flat).RootElement).Text);
        Assert.Equal("甲乙", IflytekResultParser.ParseResult(JsonDocument.Parse(nested).RootElement).Text);
    }

    [Fact]
    public void Parser_flags_errors_and_final_frames()
    {
        var error = IflytekResultParser.ParseMessage("""{"code":10165,"message":"no audio"}""");
        Assert.True(error.IsError);
        Assert.Equal(10165, error.Code);

        var final = IflytekResultParser.ParseMessage("""{"code":0,"data":{"status":2}}""");
        Assert.True(final.IsFinal);
        Assert.Null(final.Result);
    }

    [Fact]
    public void Parser_tolerates_missing_fields()
    {
        var message = IflytekResultParser.ParseMessage("""{"code":0}""");

        Assert.False(message.IsError);
        Assert.Equal(0, message.Status);
        Assert.Null(message.Result);
    }
}
