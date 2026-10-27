using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using MyAgents.Core.Workflows;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

[TestFixture]
public sealed class CheckpointerAndHistoryTests
{
    private sealed class FakeDistributedCache : IDistributedCache
    {
        public Dictionary<string, byte[]> Store { get; } = new(StringComparer.Ordinal);

        public byte[]? Get(string key) => Store.GetValueOrDefault(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            Store[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => Store.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task SqliteCheckpointer_RoundTrip_SaveLoadDelete()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"myagents_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var checkpointer = new SqliteCheckpointer(tempDb))
            {
                var snapshot = new WorkflowSnapshot(
                    WorkflowId: "test-flow",
                    StateVersion: 1,
                    SessionId: "session-sql-1",
                    StateJson: "{\"step\":\"start\"}");

                await checkpointer.SaveAsync(snapshot);

                var loaded = await checkpointer.LoadAsync("session-sql-1");
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.WorkflowId, Is.EqualTo("test-flow"));
                Assert.That(loaded.StateVersion, Is.EqualTo(1));
                Assert.That(loaded.SessionId, Is.EqualTo("session-sql-1"));
                Assert.That(loaded.StateJson, Is.EqualTo("{\"step\":\"start\"}"));

                await checkpointer.DeleteAsync("session-sql-1");
                var afterDelete = await checkpointer.LoadAsync("session-sql-1");
                Assert.That(afterDelete, Is.Null);
            }

            var disposedCheckpointer = new SqliteCheckpointer(tempDb);
            disposedCheckpointer.Dispose();

            Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await disposedCheckpointer.LoadAsync("session-sql-1"));
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { /* best effort cleanup */ }
            }
        }
    }

    [Test]
    public async Task FileSystemCheckpointer_RoundTrip_SaveLoadDelete()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"myagents_fs_{Guid.NewGuid():N}");
        try
        {
            var checkpointer = new FileSystemCheckpointer(tempDir);
            var snapshot = new WorkflowSnapshot(
                WorkflowId: "wf-fs",
                StateVersion: 2,
                SessionId: "session-fs-99",
                StateJson: "{\"counter\":42}");

            await checkpointer.SaveAsync(snapshot);

            var loaded = await checkpointer.LoadAsync("session-fs-99");
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.WorkflowId, Is.EqualTo("wf-fs"));
            Assert.That(loaded.StateVersion, Is.EqualTo(2));
            Assert.That(loaded.SessionId, Is.EqualTo("session-fs-99"));
            Assert.That(loaded.StateJson, Is.EqualTo("{\"counter\":42}"));

            await checkpointer.DeleteAsync("session-fs-99");
            var afterDelete = await checkpointer.LoadAsync("session-fs-99");
            Assert.That(afterDelete, Is.Null);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    [Test]
    public async Task FileSystemGraphHistoryStore_SavesSnapshots_ListsAndRetrievesById()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"myagents_graph_fs_{Guid.NewGuid():N}");
        try
        {
            var store = new FileSystemGraphHistoryStore(tempDir);

            var snap1 = new GraphSnapshot(
                SnapshotId: "snap-1",
                SessionId: "session-graph-1",
                WorkflowId: "graph-flow",
                NodeName: "node-a",
                StateVersion: 1,
                StateJson: "{\"stage\":1}",
                CreatedAt: DateTimeOffset.UtcNow);

            var snap2 = new GraphSnapshot(
                SnapshotId: "snap-2",
                SessionId: "session-graph-1",
                WorkflowId: "graph-flow",
                NodeName: "node-b",
                StateVersion: 2,
                StateJson: "{\"stage\":2}",
                CreatedAt: DateTimeOffset.UtcNow);

            await store.SaveSnapshotAsync(snap1);
            await store.SaveSnapshotAsync(snap2);

            var list = await store.ListSnapshotsAsync("session-graph-1");
            Assert.That(list.Count, Is.EqualTo(2));
            Assert.That(list[0].SnapshotId, Is.EqualTo("snap-1"));
            Assert.That(list[0].StateVersion, Is.EqualTo(1));
            Assert.That(list[1].SnapshotId, Is.EqualTo("snap-2"));
            Assert.That(list[1].StateVersion, Is.EqualTo(2));

            var fetched = await store.GetSnapshotAsync("snap-1");
            Assert.That(fetched, Is.Not.Null);
            Assert.That(fetched!.NodeName, Is.EqualTo("node-a"));
            Assert.That(fetched.StateJson, Is.EqualTo("{\"stage\":1}"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    [Test]
    public async Task DistributedCacheCheckpointer_SavesAndLoadsWithPrefix()
    {
        var fakeCache = new FakeDistributedCache();
        var checkpointer = new DistributedCacheCheckpointer(fakeCache, keyPrefix: "custom:prefix:");

        var snapshot = new WorkflowSnapshot(
            WorkflowId: "cache-flow",
            StateVersion: 3,
            SessionId: "sess-cache-1",
            StateJson: "{\"status\":\"cached\"}");

        await checkpointer.SaveAsync(snapshot);

        Assert.That(fakeCache.Store.ContainsKey("custom:prefix:sess-cache-1"), Is.True);

        var loaded = await checkpointer.LoadAsync("sess-cache-1");
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.WorkflowId, Is.EqualTo("cache-flow"));
        Assert.That(loaded.StateVersion, Is.EqualTo(3));
        Assert.That(loaded.SessionId, Is.EqualTo("sess-cache-1"));

        await checkpointer.DeleteAsync("sess-cache-1");
        Assert.That(fakeCache.Store.ContainsKey("custom:prefix:sess-cache-1"), Is.False);

        var afterDelete = await checkpointer.LoadAsync("sess-cache-1");
        Assert.That(afterDelete, Is.Null);
    }
}
