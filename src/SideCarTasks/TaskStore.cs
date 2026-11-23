using System.Text.Json;

namespace SideCarTasks;

/// <summary>
/// The task list's single source of truth: one JSON file, read-modify-written under a cross-process mutex
/// (the kiosk and the headless daily job can both write) and replaced atomically, so a rollover and its
/// audit entry are always saved together.
/// </summary>
public sealed class TaskStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _mutexName;

    /// <summary>Raised after any successful write, with the actor who made it.</summary>
    public event Action<Actor>? Changed;

    public TaskStore(string path)
    {
        _path = path;
        // One mutex per file, shared by every process on this PC.
        _mutexName = @"Local\SideCarTasks-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())))[..16];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public string FilePath => _path;

    /// <summary>A fresh copy of the data (safe to read without holding the lock).</summary>
    public TaskData Read() => WithLock(Load);

    /// <summary>Applies a change atomically. Throw from <paramref name="change"/> to abandon it (nothing is saved).</summary>
    public T Update<T>(Actor actor, Func<TaskData, T> change)
    {
        var result = WithLock(() =>
        {
            var data = Load();
            var r = change(data);
            Save(data);
            return r;
        });
        Changed?.Invoke(actor);
        return result;
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        try { mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { /* previous holder died; the file is still consistent (atomic replace) */ }
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private TaskData Load()
    {
        if (!File.Exists(_path)) return new TaskData();
        return JsonSerializer.Deserialize<TaskData>(File.ReadAllText(_path), Json) ?? new TaskData();
    }

    private void Save(TaskData data)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
        if (File.Exists(_path)) File.Replace(tmp, _path, null);
        else File.Move(tmp, _path);
    }
}
