using ClockManager.Api.Infrastructure.Time;

namespace ClockManager.Tests.Time;

public sealed class PayloadSanitizerTests
{
    [Fact]
    public void Removes_listed_top_level_fields_only()
    {
        var result = PayloadSanitizer.Sanitize("""{"client_ip":"203.0.113.7","nested":{"client_ip":"x"},"a":1}""", ["client_ip"]);

        Assert.Equal("""{"nested":{"client_ip":"x"},"a":1}""", result);
    }

    [Fact]
    public void Truncates_non_json_to_max_length()
    {
        var result = PayloadSanitizer.Sanitize(new string('<', 5000), ["client_ip"]);

        Assert.Equal(PayloadSanitizer.MaxLength, result.Length);
    }

    [Fact]
    public void Does_not_split_a_surrogate_pair_when_truncating()
    {
        var body = new string('a', PayloadSanitizer.MaxLength - 1) + "\U0001F600" + "tail";

        var result = PayloadSanitizer.Sanitize(body, []);

        Assert.Equal(PayloadSanitizer.MaxLength - 1, result.Length);
        Assert.False(char.IsHighSurrogate(result[^1]));
    }
}
