using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// Long work done on a thread of its own, the editor going on meanwhile: its progress printed in the
/// Output every few seconds, and what it made put in afterwards back on the main thread (where the scene
/// may be changed). The work itself must touch nothing of the scene.
/// </summary>
public sealed class RoadJob
{
    /// <summary>How often its progress is printed, in milliseconds.</summary>
    private const ulong ProgressEvery = 5000;

    /// <summary>The jobs under way, by what they are called: one of each at a time.</summary>
    private static readonly System.Collections.Generic.HashSet<string> Running = [];

    private long _done, _total = 1;

    /// <summary>
    /// How many jobs are reading a terrain now, kept on the main loop (the SceneTree) where any plugin can see it without
    /// knowing this one: while it is more than none, terrain tools are not to edit it.
    /// </summary>
    public const string TerrainReadersMeta = "unpleasant_terrain_readers";

    /// <summary>Whether some job (of any plugin) is reading a terrain now: roads are not to change it meanwhile.</summary>
    public static bool TerrainRead => Engine.GetMainLoop()?.GetMeta(TerrainReadersMeta, 0).AsInt32() > 0;

    public static void TerrainReaders(int by) =>
        Engine.GetMainLoop().SetMeta(TerrainReadersMeta, Math.Max(0, Engine.GetMainLoop().GetMeta(TerrainReadersMeta, 0).AsInt32() + by));

    /// <summary>How much there is to do, and how much is done: for the work to say as it goes (from any thread).</summary>
    public void SetTotal(long total) => Interlocked.Exchange(ref _total, Math.Max(1, total));

    public void Step(long by = 1) => Interlocked.Add(ref _done, by);

    /// <summary>
    /// Starts the work on a thread; then, on the main thread, calls <paramref name="apply"/> with what
    /// it gave (or reports what went wrong), and <paramref name="after"/> whatever happened. False if
    /// one called the same is already under way.
    /// </summary>
    public static bool Start<T>(Node anchor, string name, Func<RoadJob, T> work, Action<T> apply, Action? after = null, bool readsTerrain = false)
    {
        if (!Running.Add(name))
        {
            GD.PushWarning($"{name}: already under way; wait for it to finish.");
            after?.Invoke();
            return false;
        }

        // While it reads the terrain, the terrain is not to be edited (the terrain plugin looks at this).
        if (readsTerrain)
            TerrainReaders(+1);
        var job = new RoadJob();
        var started = Time.GetTicksMsec();
        var task = Task.Run(() => work(job));
        var shownAt = Time.GetTicksMsec();
        GD.Print($"{name}...");
        var tree = anchor.GetTree();
        Action? poll = null;
        poll = () =>
        {
            var percent = (int)(Interlocked.Read(ref job._done) * 100 / Interlocked.Read(ref job._total));
            if (!task.IsCompleted)
            {
                if (Time.GetTicksMsec() - shownAt >= ProgressEvery)
                {
                    shownAt = Time.GetTicksMsec();
                    GD.Print($"{name}... {percent}%");
                }

                return;
            }

            tree.ProcessFrame -= poll;
            Running.Remove(name);
            if (readsTerrain)
                TerrainReaders(-1);
            try
            {
                if (task.IsFaulted)
                    GD.PushError($"{name} failed: {task.Exception?.GetBaseException().Message}");
                else
                    apply(task.Result);
            }
            catch (Exception e)
            {
                GD.PushError($"{name} failed: {e.Message}");
            }
            finally
            {
                after?.Invoke();
                GD.Print($"{name}: done ({(Time.GetTicksMsec() - started) / 1000.0:0.0} s).");
            }
        };
        tree.ProcessFrame += poll;
        return true;
    }
}
