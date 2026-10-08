using Cuelight.Core;

namespace Cuelight.Tests;

/// <summary>Things that protect the person using the app: where their key and conversation may be sent, and what the log may hold.</summary>
public class ServiceAddressTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("HTTPS://Example.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://LOCALHOST:11434/v1")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://127.5.6.7/v1")]
    [InlineData("http://[::1]:11434/v1")]
    [InlineData("http://192.168.1.20:11434/v1")]
    [InlineData("http://10.0.0.5/v1")]
    [InlineData("http://172.16.0.9/v1")]
    [InlineData("http://172.31.255.1/v1")]
    [InlineData("http://169.254.10.10/v1")]
    [InlineData("http://[fd12:3456::1]/v1")]
    [InlineData("http://gaming-pc:11434/v1")]
    [InlineData("http://gaming-pc.local:11434/v1")]
    [InlineData("http://llm.home.arpa/v1")]
    public void These_addresses_are_accepted(string address) => Assert.Null(ServiceAddress.Problem(address));

    [Theory]
    [InlineData("http://api.openai.com/v1")]
    [InlineData("http://example.com/v1")]
    [InlineData("http://8.8.8.8/v1")]
    [InlineData("http://172.32.0.1/v1")]       // just outside 172.16.0.0/12
    [InlineData("http://172.15.0.1/v1")]
    [InlineData("http://192.169.1.1/v1")]
    [InlineData("http://[2001:db8::1]/v1")]
    [InlineData("http://localhost.evil.com/v1")]
    [InlineData("http://127.0.0.1.evil.com/v1")]
    public void Plain_http_to_anywhere_on_the_internet_is_refused(string address)
    {
        var problem = ServiceAddress.Problem(address);
        Assert.NotNull(problem);
        Assert.Contains("unencrypted", problem);
        Assert.Contains("https://", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://nope")]
    [InlineData("openrouter.ai/api/v1")]
    public void Things_that_are_not_web_addresses_are_refused(string address) =>
        Assert.Contains("doesn't look right", ServiceAddress.Problem(address));

    [Fact]
    public async Task A_suggestion_is_never_requested_from_a_plain_http_address_on_the_internet()
    {
        var suggester = new OpenAiCompatibleSuggester(_ => "k");
        var settings = new Settings { Provider = Provider.Other, Model = "m", BaseUrl = "http://example.com/v1" };
        var request = new SuggestionRequest("Them: Can you start Monday?", settings, Trigger.Speech, null, null);
        var ex = await Assert.ThrowsAsync<ProviderApiException>(async () =>
        {
            await foreach (var _ in suggester.StreamAsync(request, CancellationToken.None)) { }
        });
        Assert.Contains("unencrypted", ex.Message);
    }

    [Fact]
    public async Task A_key_is_not_checked_against_a_plain_http_address_on_the_internet()
    {
        var (ok, message) = await OpenAiCompatibleSuggester.ValidateKeyAsync(Provider.Other, "sk-abc", "http://example.com/v1");
        Assert.False(ok);
        Assert.Contains("unencrypted", message);
    }
}

public sealed class LogRedactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuelight-log-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous = AppLog.Directory;

    public LogRedactionTests() => AppLog.Directory = _dir;

    public void Dispose()
    {
        AppLog.Directory = _previous;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("key sk-ant-api03-AbCdEfGhIjKlMnOpQrStUv rejected", "sk-ant-api03-AbCdEf")]
    [InlineData("Incorrect API key provided: sk-proj-1234567890abcdefghij", "sk-proj-123456")]
    [InlineData("xai-AbCdEfGhIjKlMnOpQrStUvWxYz0123", "xai-AbCdEfGhIjKl")]
    [InlineData("nvapi-AbCdEfGhIjKlMnOpQrStUvWxYz0123", "nvapi-AbCdEfGhIjKl")]
    [InlineData("AIzaSyA-1234567890abcdefghijklmnopqrstu", "AIzaSyA-12345678")]
    [InlineData("Authorization: Bearer abcdef1234567890.token-part", "abcdef1234567890")]
    [InlineData("GET https://example.com/v1/models?key=SuperSecretValue123&alt=sse", "SuperSecretValue123")]
    [InlineData("https://someone:hunter2hunter2@example.com/v1", "hunter2hunter2")]
    public void Anything_that_looks_like_a_secret_is_blanked_out(string line, string secret)
    {
        var redacted = AppLog.Redact(line);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[hidden]", redacted);
    }

    [Fact]
    public void Ordinary_log_lines_are_left_alone()
    {
        const string line = "ERROR Couldn't download the speech model: HttpRequestException (Name or service not known (huggingface.co:443)) at task-runner sk-1";
        Assert.Equal(line, AppLog.Redact(line));
        Assert.Equal("Speech engine: AVX2 True · 8 logical CPUs", AppLog.Redact("Speech engine: AVX2 True · 8 logical CPUs"));
    }

    [Fact]
    public void The_file_on_disk_never_holds_the_key()
    {
        AppLog.Error("The provider said no", new InvalidOperationException("401: bad key sk-ant-api03-ZZZZZZZZZZZZZZZZZZZZZZ for https://api.example.com/v1?key=ABCDEFGHIJKLMNOP"));
        AppLog.Warn("Bearer qwertyuiopasdfghjkl");
        var text = File.ReadAllText(AppLog.FilePath);
        Assert.DoesNotContain("ZZZZZZZZZZZZ", text);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOP", text);
        Assert.DoesNotContain("qwertyuiopasdfghjkl", text);
        Assert.Contains("The provider said no", text);
        Assert.Contains("401: bad key", text);
    }
}
