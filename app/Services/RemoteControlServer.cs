using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SoundboardLab.Services;

public sealed class RemoteControlServer : IDisposable
{
    public const string DefaultPipeName = "SoundboardLab";
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private NamedPipeServerStream? listener;
    private Task? loop;
    public event Action<string>? CommandReceived;
    public event Func<string, Task<string>>? RequestReceived;
    public event Action<Exception>? Error;
    public bool IsRunning => loop is { IsCompleted: false };
    public RemoteControlServer(string pipeName = DefaultPipeName) => this.pipeName = pipeName;
    public void Start()
    {
        if (loop is not null) return;
        listener = NewListener(); // Fail synchronously when a primary instance already exists.
        loop = Task.Run(ListenAsync);
    }
    private NamedPipeServerStream NewListener() => new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = listener;
            try
            {
                pipe ??= listener = NewListener();
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromMinutes(3));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                string response;
                try {
                    string json = await ReadBoundedLineAsync(reader, deadline.Token);
                    Validate(json); CommandReceived?.Invoke(json);
                    response = RequestReceived is { } request ? await request(json).WaitAsync(deadline.Token) : "{\"ok\":true,\"status\":\"accepted\"}";
                } catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException) { response = JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
                await writer.WriteLineAsync(response.AsMemory(), deadline.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) { Error?.Invoke(ex); }
            finally {
                if (lifetime.IsCancellationRequested) { pipe?.Dispose(); listener = null; }
                else if (pipe is not null) {
                    try { if (pipe.IsConnected) pipe.Disconnect(); }
                    catch (IOException) { pipe.Dispose(); listener = null; }
                }
            }
        }
    }
    public static async Task<string> SendAsync(string json, string pipeName = DefaultPipeName, CancellationToken cancellationToken = default)
    {
        Validate(json);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await writer.WriteLineAsync(json.AsMemory(), deadline.Token);
        return await ReadBoundedLineAsync(reader, deadline.Token);
    }
    private static void Validate(string json)
    {
        if (json.Length > 65536) throw new ArgumentException("Remote command is too long.");
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Remote command must be a JSON object.");
        bool valid = parsed.RootElement.TryGetProperty("command", out var command) || parsed.RootElement.TryGetProperty("action", out command);
        if (!valid || command.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(command.GetString())) throw new InvalidDataException("Remote command requires a command or action string.");
    }
    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); char[] one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), token) != 0) {
            if (one[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length == 65536) throw new InvalidDataException("Remote command is too long."); result.Append(one[0]);
        }
        if (result.Length == 0) throw new InvalidDataException("Remote client sent no command.");
        return result.ToString();
    }
    public void Dispose() { lifetime.Cancel(); listener?.Dispose(); lifetime.Dispose(); }
}
