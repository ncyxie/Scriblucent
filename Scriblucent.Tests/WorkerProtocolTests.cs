using System.Text.Json;
using Scriblucent.AI.Protocol;

namespace Scriblucent.Tests;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void StartRequest_SerializesWithCorrectTypeDiscriminator()
    {
        var request = new WorkerStartRequest
        {
            ModelPath = "/models/test.gguf",
            ContextSize = 2048
        };

        var json = WorkerWire.SerializeRequest(request);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("start", root.GetProperty("type").GetString());
        Assert.Equal("/models/test.gguf", root.GetProperty("modelPath").GetString());
        Assert.Equal(2048, root.GetProperty("contextSize").GetInt32());
    }

    [Fact]
    public void GenerateRequest_SerializesWithCorrectTypeDiscriminator()
    {
        var request = new WorkerGenerateRequest
        {
            RequestId = "abc123",
            Prompt = "test prompt",
            MaxTokens = 500,
            Temperature = 0.3
        };

        var json = WorkerWire.SerializeRequest(request);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("generate", root.GetProperty("type").GetString());
        Assert.Equal("abc123", root.GetProperty("requestId").GetString());
        Assert.Equal("test prompt", root.GetProperty("prompt").GetString());
        Assert.Equal(500, root.GetProperty("maxTokens").GetInt32());
        Assert.Equal(0.3, root.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void StopRequest_SerializesWithCorrectTypeDiscriminator()
    {
        var request = new WorkerStopRequest();
        var json = WorkerWire.SerializeRequest(request);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("stop", root.GetProperty("type").GetString());
    }

    [Fact]
    public void StartedResponse_SerializesWithCorrectTypeDiscriminator()
    {
        var response = new WorkerStartedResponse { ModelKey = "Qwen3.5-0.8B" };
        var json = WorkerWire.SerializeResponse(response);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("started", root.GetProperty("type").GetString());
        Assert.Equal("Qwen3.5-0.8B", root.GetProperty("modelKey").GetString());
    }

    [Fact]
    public void TokenResponse_SerializesWithCorrectTypeDiscriminator()
    {
        var response = new WorkerTokenResponse
        {
            RequestId = "req1",
            Text = "hello",
            GeneratedChars = 5
        };

        var json = WorkerWire.SerializeResponse(response);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("token", root.GetProperty("type").GetString());
        Assert.Equal("req1", root.GetProperty("requestId").GetString());
        Assert.Equal("hello", root.GetProperty("text").GetString());
        Assert.Equal(5, root.GetProperty("generatedChars").GetInt32());
    }

    [Fact]
    public void CompleteResponse_SerializesWithCorrectTypeDiscriminator()
    {
        var response = new WorkerGenerateCompleteResponse
        {
            RequestId = "req1",
            Output = "full output",
            GeneratedChars = 11,
            ElapsedMs = 1234
        };

        var json = WorkerWire.SerializeResponse(response);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("complete", root.GetProperty("type").GetString());
        Assert.Equal("full output", root.GetProperty("output").GetString());
        Assert.Equal(11, root.GetProperty("generatedChars").GetInt32());
        Assert.Equal(1234, root.GetProperty("elapsedMs").GetInt64());
    }

    [Fact]
    public void ErrorResponse_SerializesWithCorrectTypeDiscriminator()
    {
        var response = new WorkerErrorResponse
        {
            RequestId = "req1",
            Message = "something went wrong"
        };

        var json = WorkerWire.SerializeResponse(response);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("error", root.GetProperty("type").GetString());
        Assert.Equal("something went wrong", root.GetProperty("message").GetString());
    }

    [Fact]
    public void DeserializeRequest_StartRequest()
    {
        var json = """{"type":"start","modelPath":"/test.gguf","contextSize":4096}""";
        var request = WorkerWire.DeserializeRequest(json);

        Assert.IsType<WorkerStartRequest>(request);
        var start = (WorkerStartRequest)request!;
        Assert.Equal("/test.gguf", start.ModelPath);
        Assert.Equal(4096, start.ContextSize);
    }

    [Fact]
    public void DeserializeRequest_GenerateRequest()
    {
        var json = """{"type":"generate","requestId":"r1","prompt":"hi","maxTokens":100,"temperature":0.5}""";
        var request = WorkerWire.DeserializeRequest(json);

        Assert.IsType<WorkerGenerateRequest>(request);
        var gen = (WorkerGenerateRequest)request!;
        Assert.Equal("r1", gen.RequestId);
        Assert.Equal("hi", gen.Prompt);
        Assert.Equal(100, gen.MaxTokens);
    }

    [Fact]
    public void DeserializeRequest_StopRequest()
    {
        var json = """{"type":"stop"}""";
        var request = WorkerWire.DeserializeRequest(json);

        Assert.IsType<WorkerStopRequest>(request);
    }

    [Fact]
    public void DeserializeResponse_StartedResponse()
    {
        var json = """{"type":"started","modelKey":"Qwen3.5-2B"}""";
        var response = WorkerWire.DeserializeResponse(json);

        Assert.IsType<WorkerStartedResponse>(response);
        Assert.Equal("Qwen3.5-2B", ((WorkerStartedResponse)response!).ModelKey);
    }

    [Fact]
    public void DeserializeResponse_ErrorResponse()
    {
        var json = """{"type":"error","requestId":"r1","message":"fail"}""";
        var response = WorkerWire.DeserializeResponse(json);

        Assert.IsType<WorkerErrorResponse>(response);
        var error = (WorkerErrorResponse)response!;
        Assert.Equal("fail", error.Message);
    }

    [Fact]
    public void RoundTrip_AllRequestTypes()
    {
        WorkerRequest[] requests =
        [
            new WorkerStartRequest { ModelPath = "/m.gguf", ContextSize = 2048 },
            new WorkerGenerateRequest { RequestId = "r1", Prompt = "test", MaxTokens = 100 },
            new WorkerStopRequest()
        ];

        foreach (var request in requests)
        {
            var json = WorkerWire.SerializeRequest(request);
            var deserialized = WorkerWire.DeserializeRequest(json);
            Assert.NotNull(deserialized);
            Assert.Equal(request.GetType(), deserialized.GetType());
        }
    }

    [Fact]
    public void RoundTrip_AllResponseTypes()
    {
        WorkerResponse[] responses =
        [
            new WorkerStartedResponse { ModelKey = "key" },
            new WorkerTokenResponse { RequestId = "r1", Text = "tok", GeneratedChars = 3 },
            new WorkerGenerateCompleteResponse { RequestId = "r1", Output = "out", GeneratedChars = 5, ElapsedMs = 100 },
            new WorkerErrorResponse { RequestId = "r1", Message = "err" }
        ];

        foreach (var response in responses)
        {
            var json = WorkerWire.SerializeResponse(response);
            var deserialized = WorkerWire.DeserializeResponse(json);
            Assert.NotNull(deserialized);
            Assert.Equal(response.GetType(), deserialized.GetType());
        }
    }
}
