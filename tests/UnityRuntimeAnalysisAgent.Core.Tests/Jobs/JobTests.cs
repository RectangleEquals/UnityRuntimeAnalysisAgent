using System.Security.Cryptography;
using System.Text;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Jobs;

public sealed class JobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uraa-tests", Guid.NewGuid().ToString("N"));
    private readonly TestHost _test = new(configure: c => c.Set(AgentConfig.MaxConcurrentJobsKey, "2"));

    private JobManager Jobs => _test.Host.Jobs;

    private static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until)
        {
            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public void At_most_MaxConcurrent_jobs_run_and_the_rest_queue()
    {
        using var gate = new ManualResetEventSlim(false);
        var refs = Enumerable.Range(0, 4).Select(i => Jobs.Start("survey", job =>
        {
            while (!gate.Wait(10, job.Cancellation))
            {
            }

            return new PingResult { Echo = $"job {i}", UptimeMs = 0 };
        })).ToList();

        Assert.True(WaitFor(() => Jobs.Running == 2));
        Assert.Equal(new[] { "running", "running", "queued", "queued" }, refs.Select(r => Jobs.Get(r.JobId).State));
        Assert.Null(Jobs.Get(refs[2].JobId).StartedAt);
        gate.Set();
        var results = refs.Select(r => Jobs.Wait(r.JobId, 5000, CancellationToken.None)).ToList();
        Assert.All(results, j => Assert.Equal("succeeded", j.State));
        Assert.Equal("job 3", ((JsonString)((JsonObject)results[3].Result!)["echo"]!).Value);
        Assert.All(results, j => Assert.NotNull(j.FinishedAt));
        Assert.Equal(4, Jobs.List("succeeded").Count);
    }

    [Fact]
    public void Cancelling_a_running_or_queued_job()
    {
        var started = new ManualResetEventSlim(false);
        var running = Jobs.Start("trace", job =>
        {
            started.Set();
            while (true)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }
        });
        Assert.True(started.Wait(5000, TestContext.Current.CancellationToken));
        var cancel = Jobs.Cancel(running.JobId);
        Assert.True(cancel.Cancelled);
        var finished = Jobs.Wait(running.JobId, 5000, CancellationToken.None);
        Assert.Equal("cancelled", finished.State);
        Assert.Equal(ErrorCodes.Cancelled, finished.Error!.Code);
        Assert.False(Jobs.Cancel(running.JobId).Cancelled); // already finished

        var failing = Jobs.Start("broken", _ => throw new InvalidOperationException("job bug"));
        var failed = Jobs.Wait(failing.JobId, 5000, CancellationToken.None);
        Assert.Equal("failed", failed.State);
        Assert.Equal(ErrorCodes.Internal, failed.Error!.Code);
        Assert.Throws<ProtocolException>(() => Jobs.Get("j-999"));
    }

    [Fact]
    public void Progress_and_finish_events_reach_subscribers_with_the_context()
    {
        var peer = _test.Connect();
        peer.Call(Methods.EventsSubscribe, "{\"kinds\":[\"job.progress\",\"job.finished\"]}");
        var context = (JsonObject)JsonValue.Parse("{\"finding\":\"F-1\"}");
        var job = Jobs.Start("survey", j =>
        {
            for (var i = 0; i < 100; i++)
            {
                j.Progress("types", i, 100);
            }

            return null;
        }, context);

        var finished = JobFinishedEventParams.Read(peer.AwaitEvent(EventKinds.JobFinished).Params, "params");
        Assert.Equal(job.JobId, finished.Job.JobId);
        Assert.Equal("succeeded", finished.Job.State);
        var progress = peer.Events.Where(e => e.Method == EventKinds.JobProgress).ToList();
        Assert.InRange(progress.Count, 1, 3); // 100 reports, throttled
        Assert.Equal("{\"finding\":\"F-1\"}", progress[0].Context!.ToString());
        Assert.Equal(99, Jobs.Get(job.JobId).Progress!.Done); // job.get always shows the latest
    }

    [Fact]
    public void Jobs_run_chunks_on_the_main_thread()
    {
        var job = Jobs.Start("content.scan", j => new PingResult
        {
            Echo = string.Join(" ", j.RunOnMain(() => new List<string> { "on", "main" })), // a collection result isn't a routine
            UptimeMs = 0,
            Frame = j.RunOnMain(() => _test.Unity.Frame),
        });
        _test.Unity.StepUntil(() => Jobs.Get(job.JobId).State == "succeeded");
        Assert.True(((JsonNumber)((JsonObject)Jobs.Get(job.JobId).Result!)["frame"]!).TryGetInt64(out var frame) && frame > 0);
    }

    [Fact]
    public void Job_methods_through_the_protocol()
    {
        var service = new TestService { Jobs = Jobs };
        service.JobGate.Reset();
        _test.Host.Dispatcher.Register(service);
        var peer = _test.Connect();
        var jobRef = JobRef.Read(peer.Call(Methods.SurveyStart, "{\"outFile\":\"X:/Example/survey.ndjson\"}").Result, "result");
        Assert.Equal("survey", jobRef.Kind);
        // A short wait answers with the current state; the job may still be queued on a busy machine, so poll until it runs.
        var early = JobInfo.Read(peer.Call(Methods.JobWait, $"{{\"jobId\":\"{jobRef.JobId}\",\"timeoutMs\":50}}").Result, "result");
        for (var deadline = DateTime.UtcNow.AddSeconds(10); early.State == "queued" && DateTime.UtcNow < deadline;)
        {
            early = JobInfo.Read(peer.Call(Methods.JobWait, $"{{\"jobId\":\"{jobRef.JobId}\",\"timeoutMs\":50}}").Result, "result");
        }

        Assert.Equal("running", early.State);
        Assert.Single(JobListResult.Read(peer.Call(Methods.JobList, "{\"state\":\"running\"}").Result, "result").Items);
        service.JobGate.Set();
        var done = JobInfo.Read(peer.Call(Methods.JobWait, $"{{\"jobId\":\"{jobRef.JobId}\",\"timeoutMs\":5000}}").Result, "result");
        Assert.Equal("succeeded", done.State);
        Assert.Equal("done", done.Progress!.Phase);
        var cancel = JobCancelResult.Read(peer.Call(Methods.JobCancel, $"{{\"jobId\":\"{jobRef.JobId}\"}}").Result, "result");
        Assert.False(cancel.Cancelled);
        Assert.Equal("succeeded", cancel.State);
        Assert.Equal(ErrorCodes.NotFound, peer.Call(Methods.JobGet, "{\"jobId\":\"j-404\"}").Error!.Code);
    }

    [Fact]
    public void The_ndjson_footer_counts_records_and_hashes_every_preceding_line()
    {
        var path = Path.Combine(_dir, "out", "survey.ndjson");
        OutputFile file;
        using (var writer = new NdjsonFileWriter(path, "survey", "1", "0.1.0", (JsonObject)JsonValue.Parse("{\"game\":\"x\"}")))
        {
            writer.Write((JsonObject)JsonValue.Parse("{\"rec\":\"type\",\"name\":\"A\"}"));
            writer.Write((JsonObject)JsonValue.Parse("{\"rec\":\"type\",\"name\":\"B\"}"));
            writer.Write((JsonObject)JsonValue.Parse("{\"rec\":\"member\",\"name\":\"C\"}"));
            writer.CountRedaction("maxItems");
            Assert.True(File.Exists(path + ".partial"));
            Assert.False(File.Exists(path));
            file = writer.Complete();
        }

        Assert.False(File.Exists(path + ".partial"));
        var bytes = File.ReadAllBytes(path);
        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        var header = (JsonObject)JsonValue.Parse(lines[0]);
        Assert.Equal("header", ((JsonString)header["rec"]!).Value);
        var footer = (JsonObject)JsonValue.Parse(lines[^1]);
        Assert.Equal("{\"member\":1,\"type\":2}", footer["counts"]!.ToString());
        Assert.Equal("{\"maxItems\":1}", footer["redactions"]!.ToString());

        var body = Encoding.UTF8.GetBytes(string.Concat(lines.Take(4).Select(l => l + "\n")));
        var expected = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        Assert.Equal(expected, ((JsonString)footer["sha256"]!).Value);
        Assert.Equal(expected, file.Sha256);
        Assert.Equal(bytes.Length, file.Bytes);
        Assert.Equal(path, file.Path);
    }

    [Fact]
    public void A_failed_or_cancelled_file_leaves_nothing_behind()
    {
        var path = Path.Combine(_dir, "failed.ndjson");
        using (var writer = new NdjsonFileWriter(path, "trace", "1", "0.1.0"))
        {
            writer.Write((JsonObject)JsonValue.Parse("{\"rec\":\"enter\"}"));
            // disposed without Complete: the job failed or was cancelled
        }

        Assert.False(File.Exists(path + ".partial"));
        Assert.False(File.Exists(path));
        Assert.Equal(ErrorCodes.InvalidParams, Assert.Throws<ProtocolException>(() => new NdjsonFileWriter("relative.ndjson", "x", "1", "0")).Code);
        using var w = new NdjsonFileWriter(Path.Combine(_dir, "x.ndjson"), "x", "1", "0");
        Assert.Throws<ArgumentException>(() => w.Write((JsonObject)JsonValue.Parse("{\"rec\":\"footer\"}")));
        Assert.Throws<ArgumentException>(() => w.Write((JsonObject)JsonValue.Parse("{\"name\":\"no rec\"}")));
    }

    public void Dispose()
    {
        _test.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
