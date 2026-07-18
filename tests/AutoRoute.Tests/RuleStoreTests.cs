using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.Engine;
using AutoRoute.Engine.Model;

namespace AutoRoute.Tests;

public sealed class RuleStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public RuleStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "autoroute-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "rules.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static RulesDocument SampleDoc(int ruleCount = 1) => new(
        RulesDocument.CurrentVersion,
        Enumerable.Range(0, ruleCount).Select(i => new Rule(
            Id: $"r{i}", Name: $"rule {i}", Enabled: true,
            Source: new MatchCriteria(new[] { new Predicate(Field.ApplicationName, Op.Equals, "Zen") }),
            Target: new MatchCriteria(new[] { new Predicate(Field.NodeName, Op.Equals, "GameSink") }))).ToList(),
        new[]
        {
            new Suppression("s0",
                new MatchCriteria(new[] { new Predicate(Field.ApplicationName, Op.Equals, "Zen") }),
                new MatchCriteria(new[] { new Predicate(Field.NodeName, Op.Contains, "analog") })),
        },
        new[]
        {
            new ProtectedMatch("p0",
                new MatchCriteria(new[] { new Predicate(Field.MediaClass, Op.Regex, "^Stream/Input") })),
        });

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task LoadAsync_missing_file_returns_empty()
    {
        using var store = new RuleStore(_path);
        var doc = await store.LoadAsync();

        Assert.Empty(doc.Rules);
        Assert.Empty(doc.Suppressions);
        Assert.Empty(doc.Protected);
        Assert.Equal(RulesDocument.CurrentVersion, doc.Version);
        Assert.Same(doc, store.Current);
    }

    [Fact]
    public async Task SaveAsync_then_reload_in_fresh_store_roundtrips()
    {
        var original = SampleDoc(ruleCount: 2);

        using (var writer = new RuleStore(_path))
            await writer.SaveAsync(original);

        using var reader = new RuleStore(_path);
        var loaded = await reader.LoadAsync();

        Assert.Equal(original.Version, loaded.Version);
        Assert.Equal(2, loaded.Rules.Count);
        Assert.Equal("r0", loaded.Rules[0].Id);
        Assert.Equal("Zen", loaded.Rules[0].Source.Predicates[0].Value);
        Assert.Equal(Field.ApplicationName, loaded.Rules[0].Source.Predicates[0].Field);
        Assert.Equal(Op.Equals, loaded.Rules[0].Source.Predicates[0].Op);
        Assert.Single(loaded.Suppressions);
        Assert.Equal(Op.Contains, loaded.Suppressions[0].Target.Predicates[0].Op);
        Assert.Single(loaded.Protected);
        Assert.Equal(Op.Regex, loaded.Protected[0].Match.Predicates[0].Op);
    }

    [Fact]
    public async Task SaveAsync_writes_frozen_keys_and_enum_names()
    {
        using var store = new RuleStore(_path);
        await store.SaveAsync(SampleDoc());

        var json = await File.ReadAllTextAsync(_path);

        // On-disk key names from PLAN's schema.
        foreach (var key in new[] { "\"version\"", "\"rules\"", "\"suppressions\"", "\"protected\"",
                                    "\"predicates\"", "\"field\"", "\"op\"", "\"value\"", "\"enabled\"" })
            Assert.Contains(key, json);

        // Enums serialized by NAME, not number.
        Assert.Contains("\"ApplicationName\"", json);
        Assert.Contains("\"Equals\"", json);
        Assert.DoesNotContain("\"field\": 0", json);
    }

    [Fact]
    public async Task SaveAsync_is_atomic_and_leaves_no_temp_files()
    {
        using var store = new RuleStore(_path);
        await store.SaveAsync(SampleDoc());
        await store.SaveAsync(SampleDoc(ruleCount: 3)); // overwrite in place

        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "rules.json" }, files); // no leftover *.tmp-* siblings
    }

    [Fact]
    public async Task Malformed_file_keeps_last_good_and_does_not_throw()
    {
        await File.WriteAllTextAsync(_path, "{ this is not valid json ]");

        using var store = new RuleStore(_path);
        var doc = await store.LoadAsync(); // must not throw

        Assert.Same(RulesDocument.Empty, store.Current); // fell back to last-good (empty)
        Assert.Empty(doc.Rules);
    }

    [Fact]
    public async Task External_edit_raises_Changed_hot_reload()
    {
        using var store = new RuleStore(_path);
        await store.LoadAsync();

        RulesDocument? observed = null;
        store.Changed += (_, d) => observed = d;

        // Simulate an editor writing a different policy directly to disk.
        const string external = """
        {
          "version": 1,
          "rules": [
            { "id": "ext", "name": "external", "enabled": true,
              "source": { "predicates": [ { "field": "ApplicationName", "op": "Equals", "value": "Zen" } ] },
              "target": { "predicates": [ { "field": "NodeName", "op": "Equals", "value": "GameSink" } ] } }
          ],
          "suppressions": [],
          "protected": []
        }
        """;
        await File.WriteAllTextAsync(_path, external);

        Assert.True(await WaitUntil(() => observed is not null), "Changed did not fire for the external edit");
        Assert.Single(observed!.Rules);
        Assert.Equal("ext", observed!.Rules[0].Id);
        Assert.Equal("ext", store.Current.Rules[0].Id);
    }

    [Fact]
    public async Task Own_atomic_save_does_not_feedback_loop()
    {
        using var store = new RuleStore(_path);
        await store.LoadAsync();

        var changedCount = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changedCount);

        await store.SaveAsync(SampleDoc());

        // The explicit save raises Changed exactly once; the watcher must ignore our own write.
        await Task.Delay(1200); // longer than the reload debounce
        Assert.Equal(1, changedCount);
    }
}
