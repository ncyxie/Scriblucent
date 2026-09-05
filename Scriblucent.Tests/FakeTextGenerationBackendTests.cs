using Scriblucent.Services.AI;

namespace Scriblucent.Tests;

public sealed class FakeTextGenerationBackendTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsEnqueuedResponses()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("q: What is 2+2?\na: 4");
        backend.EnqueueResponse("q: What is 3+3?\na: 6");

        var response1 = await backend.GenerateAsync(new AiGenerationRequest("prompt1", 100, 0.5));
        var response2 = await backend.GenerateAsync(new AiGenerationRequest("prompt2", 100, 0.5));

        Assert.Equal("q: What is 2+2?\na: 4", response1.Output);
        Assert.Equal("q: What is 3+3?\na: 6", response2.Output);
        Assert.Equal(2, backend.GenerateCallCount);
    }

    [Fact]
    public async Task GenerateAsync_WithNoResponses_ThrowsInvalidOperationException()
    {
        using var backend = new FakeTextGenerationBackend();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => backend.GenerateAsync(new AiGenerationRequest("prompt", 100, 0.5)));
    }

    [Fact]
    public async Task GenerateAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("output");
        backend.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => backend.GenerateAsync(new AiGenerationRequest("prompt", 100, 0.5)));
    }

    [Fact]
    public async Task OpenAsync_IncrementsCallCount()
    {
        using var backend = new FakeTextGenerationBackend();

        await backend.OpenAsync();
        await backend.OpenAsync();

        Assert.Equal(2, backend.OpenCallCount);
    }

    [Fact]
    public async Task GenerateAsync_RecordsRequests()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponse("output1");
        backend.EnqueueResponse("output2");

        await backend.GenerateAsync(new AiGenerationRequest("prompt1", 100, 0.5));
        await backend.GenerateAsync(new AiGenerationRequest("prompt2", 200, 0.3));

        Assert.Equal(2, backend.Requests.Count);
        Assert.Equal("prompt1", backend.Requests[0].Prompt);
        Assert.Equal(100, backend.Requests[0].MaxTokens);
        Assert.Equal(0.5, backend.Requests[0].Temperature);
        Assert.Equal("prompt2", backend.Requests[1].Prompt);
        Assert.Equal(200, backend.Requests[1].MaxTokens);
    }

    [Fact]
    public async Task EnqueueResponses_MultipleOutputs()
    {
        using var backend = new FakeTextGenerationBackend();
        backend.EnqueueResponses("a", "b", "c");

        var r1 = await backend.GenerateAsync(new AiGenerationRequest("p", 100, 0.5));
        var r2 = await backend.GenerateAsync(new AiGenerationRequest("p", 100, 0.5));
        var r3 = await backend.GenerateAsync(new AiGenerationRequest("p", 100, 0.5));

        Assert.Equal("a", r1.Output);
        Assert.Equal("b", r2.Output);
        Assert.Equal("c", r3.Output);
    }
}
