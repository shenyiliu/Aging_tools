using Aging.Core;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Aging.Windows;

public sealed record WorkerRequest(int Version, RunSnapshot Run);
public sealed record WorkerMessage(string Type, string Text, TaskResult? Result = null, List<string>? Reports = null);

/// <summary>Current-user named pipe. The host alone writes checkpoints; the worker owns engine processes.</summary>
public sealed class WorkerBridge(string executable)
{
    private readonly OwnedProcessRunner runner = new();
    public async Task<TaskResult> ExecuteAsync(AgingItem item, RunSnapshot run, Action<string> progress, CancellationToken ct)
    {
        var pipeName = "GW_Aging_" + Guid.NewGuid().ToString("N");
        string request = Path.Combine(run.LogDirectory, $"worker-{run.CurrentIndex + 1}-{Guid.NewGuid():N}.json");
        AtomicFile.Write(request, JsonSerializer.SerializeToUtf8Bytes(new WorkerRequest(1, JsonData.Clone(run)), JsonData.Options), false);
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var linked = new CancellationTokenSource();
        TimeSpan max = item.IsStress ? TimeSpan.FromHours(item.Hours) + TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(item.SleepSeconds + 360);
        var process = runner.RunAsync(executable, ["--worker", "--request", request, "--pipe", pipeName], AppContext.BaseDirectory,
            Path.Combine(run.LogDirectory, $"worker-{run.CurrentIndex + 1}"), max, linked.Token);
        try
        {
            using var connectLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectLimit.CancelAfter(TimeSpan.FromSeconds(20));
            Task connection = pipe.WaitForConnectionAsync(connectLimit.Token);
            if (await Task.WhenAny(connection, process) == process && !pipe.IsConnected)
            {
                connectLimit.Cancel();
                try { await connection; } catch (OperationCanceledException) { }
                throw new IOException("Worker 在管道连接前退出");
            }
            await connection;
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(max);
            WorkerMessage? final = null;
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                var message = JsonSerializer.Deserialize<WorkerMessage>(line, JsonData.Options) ?? throw new IOException("Worker 返回空消息");
                if (message.Type == "progress") progress(message.Text);
                else if (message.Type == "result") { final = message; break; }
                else if (message.Type == "error") throw new IOException("Worker：" + message.Text);
                else throw new IOException("无法识别 Worker 消息");
            }
            var exit = await process;
            if (exit.ExitCode != 0 || final?.Result == null) throw new IOException("Worker 异常退出或未返回明确结果");
            if (final.Reports != null) run.Items[run.CurrentIndex].Reports.AddRange(final.Reports);
            return final.Result;
        }
        finally
        {
            if (ct.IsCancellationRequested && pipe.IsConnected)
            {
                try
                {
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes("stop\n"), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
                    await pipe.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
                    await process.WaitAsync(TimeSpan.FromSeconds(4));
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { /* job closure is the fallback */ }
            }
            linked.Cancel();
            try { await process; } catch (Exception) { /* execution failure/cancellation handled by coordinator */ }
            // Keep the immutable request as diagnostic evidence. Never use it to restart work automatically.
        }
    }

    public static async Task<int> RunWorkerAsync(string requestPath, string pipeName, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(15000, ct);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var commandReader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        Task commands = WatchCommandsAsync(commandReader, execution);
        var compact = new JsonSerializerOptions(JsonData.Options) { WriteIndented = false };
        void Send(WorkerMessage message) => writer.WriteLine(JsonSerializer.Serialize(message, compact));
        try
        {
            var request = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllBytesAsync(requestPath, ct), JsonData.Options) ?? throw new IOException("空 Worker 请求");
            var run = request.Run;
            if (request.Version != 1 || run.Simulation || run.CurrentIndex < 0 || run.CurrentIndex >= run.Items.Count) throw new InvalidDataException("Worker 请求版本/内容无效");
            var item = run.Items[run.CurrentIndex].Item;
            if (item.Kind is TaskKind.Reboot or TaskKind.Video) throw new InvalidDataException("重启和交互视频由宿主协调");
            var platform = new WindowsPlatform();
            var preflight = await platform.PreflightAsync(item, run, execution.Token);
            if (!preflight.CanRun) throw new IOException(string.Join("；", preflight.Messages));
            var result = await platform.ExecuteAsync(item, run, text => Send(new("progress", text)), execution.Token);
            Send(new("result", "", result, run.Items[run.CurrentIndex].Reports));
            return 0;
        }
        catch (OperationCanceledException) { Send(new("result", "", new(Outcome.Aborted, "Worker 已停止"))); return 0; }
        catch (Exception e) { Send(new("error", e.Message)); return 1; }
        finally
        {
            execution.Cancel();
            try { await commands; } catch (OperationCanceledException) { }
        }
    }
    private static async Task WatchCommandsAsync(StreamReader reader, CancellationTokenSource execution)
    {
        try
        {
            while (await reader.ReadLineAsync(execution.Token) is { } command)
                if (command == "stop") { execution.Cancel(); return; }
            execution.Cancel(); // Host disconnected: stop the owned engine.
        }
        catch (IOException) { execution.Cancel(); }
    }
}
