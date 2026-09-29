using System.Text.RegularExpressions;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Tests.Support;
using Zoo.Il;
using Zoo.Survey;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Code;

/// <summary>
/// The survey and IL index jobs over the test assembly: golden NDJSON (with build- and compiler-dependent values
/// scrubbed; exact IL is checked against dnlib elsewhere), the records that matter, caching, and cancellation.
/// </summary>
public sealed partial class BulkJobTests : IDisposable
{
    private const string Zoo = "UnityRuntimeAnalysisAgent.TestAssemblies";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uraa-tests", Guid.NewGuid().ToString("N"));
    private readonly TestHost _test = new();
    private readonly WirePeer _peer;

    public BulkJobTests()
    {
        _peer = _test.Connect();
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task The_il_index()
    {
        var path = Path.Combine(_dir, "il.ndjson");
        var result = RunJob(Methods.IlIndexStart, $"{{\"outFile\":{Json(path)},\"include\":[\"{Zoo}\"]}}");
        var file = IlIndexStartJobResult.Read(result, "result");
        Assert.False(file.Cached);
        var lines = File.ReadAllLines(path);
        Assert.Equal(file.File.Sha256, ((JsonString)((JsonObject)JsonValue.Parse(lines[^1]))["sha256"]!).Value);
        var records = lines.Select(l => (JsonObject)JsonValue.Parse(l)).ToList();

        Assert.Contains(records, r => Rec(r) == "call" && Name(r["caller"]) == "Zoo.Il.Corpus::FunctionPointer(System.Int32)" && Name(r["callee"]) == "Zoo.Il.Corpus::Twice(System.Int32)" && Text(r["opcode"]) == "ldftn");
        Assert.Contains(records, r => Rec(r) == "string" && Text(r["literal"]) == "item_added");
        Assert.Contains(records, r => Rec(r) == "field" && Name(r["field"]) == "Zoo.Il.Corpus::counter" && Text(r["access"]) == "write" && r["static"] is JsonBoolean { Value: true });
        Assert.Contains(records, r => Rec(r) == "alloc" && Text(r["opcode"]) == "newarr" && Name(r["type"]) == "System.Int32");
        Assert.Contains(records, r => Rec(r) == "typeref" && Text(r["usage"]) == "castclass" && Name(r["type"]) == "Zoo.Il.Square");
        Assert.DoesNotContain(records, r => Rec(r) == "call" && ((JsonObject)r["callee"]!).ContainsKey("error")); // array methods left out, counted

        var again = IlIndexStartJobResult.Read(RunJob(Methods.IlIndexStart, $"{{\"outFile\":{Json(path)},\"include\":[\"{Zoo}\"]}}"), "result");
        Assert.True(again.Cached);
        Assert.Equal(file.File.Sha256, again.File.Sha256);
        var callsOnly = IlIndexStartJobResult.Read(RunJob(Methods.IlIndexStart, $"{{\"outFile\":{Json(path)},\"include\":[\"{Zoo}\"],\"records\":[\"calls\",\"tokens\"]}}"), "result");
        Assert.False(callsOnly.Cached); // other records: another key
        Assert.All(File.ReadLines(path).Skip(1).SkipLast(1), l => Assert.Contains("\"token\":", l));

        await Verify(Scrub(string.Join("\n", lines)), "txt");
    }

    [Fact]
    public async Task The_survey()
    {
        var hero = _test.Unity.World.Create("Hero");
        hero.AddComponent<Enemy>();
        _test.Unity.World.Create("Boss").AddComponent<Enemy>();
        _test.Unity.World.Assets.Add(new ItemDatabase { name = "Items" });
        var path = Path.Combine(_dir, "survey.ndjson");
        var parameters = $"{{\"outFile\":{Json(path)},\"include\":[\"{Zoo}\"],\"serializerMarkers\":{{\"attributes\":[\"Zoo.Survey.FixtureSerializedAttribute\"]}},\"countInstances\":true}}";
        SurveyStartJobResult.Read(RunJob(Methods.SurveyStart, parameters), "result");
        var full = File.ReadAllLines(path);
        var records = full.Select(l => (JsonObject)JsonValue.Parse(l)).ToList();

        Assert.Equal(["header", "assembly"], records.Take(2).Select(Rec));
        var enemy = records.Single(r => Rec(r) == "type" && Text(r["fullName"]) == "Zoo.Survey.Enemy");
        Assert.Equal("[\"unityComponent\"]", enemy["flags"]!.ToString());
        Assert.Equal(["Awake", "Update", "OnTriggerEnter2D"], records.Where(r => Rec(r) == "unity_message" && Name(r["type"]) == "Zoo.Survey.Enemy").Select(r => Text(r["message"])));
        string Verdict(string field) => Text(records.Single(r => Rec(r) == "serialized_field" && Name(r["field"]) == "Zoo.Survey.Enemy::" + field)["verdict"]);
        Assert.Equal("serialized", Verdict("health"));
        Assert.Equal("serialized", Verdict("id")); // [SerializeField] private
        Assert.Equal("nonSerialized", Verdict("temporary"));
        Assert.Equal("nonSerialized", Verdict("readOnly"));
        Assert.Equal("serialized", Verdict("stats")); // List<[Serializable]>
        Assert.Equal("serialized", Verdict("statsArray"));
        Assert.Equal("not_serializable_type", Verdict("map"));
        Assert.Equal("serialized", Verdict("shape")); // [SerializeReference] to an interface
        Assert.Equal("not_serializable_type", Verdict("generic")); // Box<int> isn't [Serializable]
        Assert.Equal("not_serializable_type", Verdict("nested")); // List<List<int>>
        Assert.DoesNotContain(records, r => Rec(r) == "serialized_field" && Name(r["field"]) is "Zoo.Survey.Enemy::secret" or "Zoo.Survey.Enemy::count");

        string? Singleton(string member) => (records.Single(r => Rec(r) == "static" && Name(r["member"]) == member)["reason"] as JsonString)?.Value;
        Assert.Equal("self-typed static", Singleton("Zoo.Survey.GameManager::Instance"));
        Assert.Equal("generic singleton base", Singleton("Zoo.Survey.Singleton`1::Current"));
        Assert.Equal("Instance naming", Singleton("Zoo.Survey.Services::Instance"));
        Assert.Null(Singleton("Zoo.Survey.Enemy::count"));
        Assert.DoesNotContain(records, r => Rec(r) == "static" && Name(r["member"]) == "Zoo.Survey.Services::Limit"); // const

        var custom = records.Where(r => Rec(r) == "custom_serializer").ToList();
        var marked = Assert.Single(custom); // the marked type, with the fields Unity leaves out (its marked field among them)
        Assert.Equal(["Zoo.Survey.Custom::table"], ((JsonArray)marked["members"]!).Select(m => Name(m)));

        int Count(string type) => (int)((JsonNumber)records.Single(r => Rec(r) == "instance_count" && Name(r["type"]) == type)["count"]!).GetDouble();
        Assert.Equal(2, Count("Zoo.Survey.Enemy"));
        Assert.Equal(1, Count("Zoo.Survey.ItemDatabase"));
        Assert.All(records.Where(r => Rec(r) == "member" && Text(r["kind"]) is "method" or "constructor" && r["hasBody"] is JsonBoolean { Value: true }),
            r => Assert.Matches("^[0-9a-f]{64}$", Text(r["ilHash"])));

        var cached = SurveyStartJobResult.Read(RunJob(Methods.SurveyStart, $"{{\"outFile\":{Json(path)},\"include\":[\"{Zoo}\"]}}"), "result");
        var reused = SurveyStartJobResult.Read(RunJob(Methods.SurveyStart, $"{{\"outFile\":{Json(path)},\"include\":[\"{Zoo}\"]}}"), "result");
        Assert.True(reused.Extra?["cached"] is JsonBoolean { Value: true });
        Assert.Equal(cached.File.Sha256, reused.File.Sha256);
        var forced = SurveyStartJobResult.Read(RunJob(Methods.SurveyStart, $"{{\"outFile\":{Json(path)},\"include\":[\"{Zoo}\"],\"force\":true}}"), "result");
        Assert.Null(forced.Extra?["cached"]);

        await Verify(Scrub(string.Join("\n", full)), "txt");
    }

    [Fact]
    public void Bad_parameters_and_cancellation_leave_no_file()
    {
        var path = Path.Combine(_dir, "cancelled.ndjson");
        var job = JobRef.Read(Call(Methods.IlIndexStart, $"{{\"outFile\":{Json(path)}}}"), "result"); // everything loaded: long enough to cancel
        Assert.True(JobCancelResult.Read(Call(Methods.JobCancel, $"{{\"jobId\":\"{job.JobId}\"}}"), "result").Cancelled || _test.Host.Jobs.Get(job.JobId).State == "succeeded");
        var finished = _test.Host.Jobs.Wait(job.JobId, 60_000, CancellationToken.None);
        Assert.Contains(finished.State, new[] { "cancelled", "succeeded" });
        if (finished.State == "cancelled")
        {
            Assert.False(File.Exists(path));
        }

        Assert.False(File.Exists(path + ".partial"));

        var relative = JobRef.Read(Call(Methods.SurveyStart, "{\"outFile\":\"relative.ndjson\"}"), "result");
        Assert.Equal(ErrorCodes.InvalidParams, _test.Host.Jobs.Wait(relative.JobId, 60_000, CancellationToken.None).Error!.Code);
        var badCount = JobRef.Read(Call(Methods.SurveyStart, $"{{\"outFile\":{Json(Path.Combine(_dir, "x.ndjson"))},\"countInstances\":\"many\"}}"), "result");
        Assert.Equal(ErrorCodes.InvalidParams, _test.Host.Jobs.Wait(badCount.JobId, 60_000, CancellationToken.None).Error!.Code);
    }

    public void Dispose()
    {
        _test.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    // Starts a job and runs frames until it's done (the survey counts instances on the main thread).
    private JsonValue RunJob(string method, string parameters)
    {
        var job = JobRef.Read(Call(method, parameters), "result");
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (_test.Host.Jobs.Get(job.JobId).State is "queued" or "running" && DateTime.UtcNow < deadline)
        {
            _test.Unity.StepFrames();
            Thread.Sleep(2);
        }

        var info = _test.Host.Jobs.Get(job.JobId);
        Assert.True(info.State == "succeeded", $"{method}: {info.State} {info.Error?.Code} {info.Error?.Message}");
        return info.Result!;
    }

    private JsonValue Call(string method, string parameters)
    {
        var response = _peer.Call(method, parameters, timeoutMs: 60_000);
        Assert.True(response.Error is null, $"{method}: {response.Error?.Code} {response.Error?.Message}");
        return response.Result!;
    }

    private static string Json(string text) => new JsonString(text).ToString();

    private static string Rec(JsonObject record) => Text(record["rec"]);

    private static string Text(JsonValue? value) => ((JsonString)value!).Value;

    private static string Name(JsonValue? anchor) => anchor is JsonObject a ? Text(a["name"]) : Text(anchor);

    // What depends on the build, the machine, the time or the compiler.
    private static string Scrub(string ndjson)
    {
        foreach (var (pattern, replacement) in new[]
        {
            ("\"(mvid|cacheKey|sha256|ilHash|createdAt|agentVersion|location)\":\"[^\"]*\"", "\"$1\":\"…\""),
            ("\"(token|offset|ilSize|durationMs)\":\\d+", "\"$1\":0"),
        })
        {
            ndjson = Regex.Replace(ndjson, pattern, replacement);
        }

        return ndjson;
    }
}
