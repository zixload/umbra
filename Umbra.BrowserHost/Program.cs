using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Umbra.Core;

try
{
    var input = Console.OpenStandardInput();
    var output = Console.OpenStandardOutput();
    var lengthBytes = new byte[4];

    while (!BrowserHostLifecycle.IsStopRequested() && await ReadExactlyAsync(input, lengthBytes))
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > 1024 * 1024) break;
        var payload = new byte[length];
        if (!await ReadExactlyAsync(input, payload)) break;
        if (BrowserHostLifecycle.IsStopRequested()) break;

        object response;
        string? requestId = null;
        try
        {
            using var message = JsonDocument.Parse(payload);
            var action = message.RootElement.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : "getState";
            requestId = message.RootElement.TryGetProperty("requestId", out var requestIdElement)
                ? requestIdElement.GetString()
                : null;
            if (action == "recordAttempt" && message.RootElement.TryGetProperty("target", out var targetElement))
            {
                BlockAttemptHistory.Record(targetElement.GetString() ?? "", "site");
                response = new { ok = true, requestId };
            }
            else if (action == "stopSession")
            {
                var stopped = BrowserSessionControl.TryStop(out var session, out var error);
                response = CreateStateResponse(requestId, stopped, error, session);
            }
            else
            {
                response = CreateStateResponse(requestId, true, null, BrowserSessionControl.GetStatus());
            }
        }
        catch (Exception error)
        {
            response = new { ok = false, error = error.Message, requestId };
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(response, Json.Options);
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, json.Length);
        await output.WriteAsync(lengthBytes);
        await output.WriteAsync(json);
        await output.FlushAsync();
    }
}
catch (Exception error)
{
    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
    CrashReporter.Write(error, "browser-host", version?.ToString(3));
}

static object CreateStateResponse(string? requestId, bool ok, string? error, BrowserSessionStatus session)
{
    var state = BrowserBlockingState.GetCurrent();
    return new
    {
        ok,
        error,
        requestId,
        blocking = state.Blocking,
        sites = state.Sites,
        session,
    };
}

static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        if (BrowserHostLifecycle.IsStopRequested()) return false;
        var readTask = stream.ReadAsync(buffer.AsMemory(offset)).AsTask();
        while (!readTask.IsCompleted)
        {
            await Task.WhenAny(readTask, Task.Delay(200));
            if (BrowserHostLifecycle.IsStopRequested()) return false;
        }
        var read = await readTask;
        if (read == 0) return false;
        offset += read;
    }
    return true;
}
