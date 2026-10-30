using System.Text.Json;

namespace Anima.Launcher.Core;

/// <summary>Durable intent and bounded file scheduling. Owners remain responsible for install/package transactions.</summary>
public sealed partial class DownloadTaskService : IDownloadDispatcher, IAsyncDisposable
{
    private readonly Downloads downloads;
    private readonly DownloadTaskStore store;
    private readonly DownloadTaskDatabase database;
    private readonly object sync = new();
    private readonly AsyncLocal<OwnerContext?> context = new();
    private readonly Dictionary<string, Execution> executions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnerExecution> ownerExecutions = new(StringComparer.Ordinal);
    private readonly List<Execution> pending = [];
    private readonly HashSet<string> cleaning = new(StringComparer.Ordinal);
    private readonly HashSet<string> cleaningTargets = new(StringComparer.Ordinal);
    private readonly int maxConcurrent;
    private readonly int maxDependencyConcurrent;
    private readonly Func<DependencyDownloadIdentity, LauncherConfig, CancellationToken, Task<string[]>> dependencySourceResolver;
    private int running;
    private long sequence;
    private bool disposed;
    private bool closing;
    public event Action? Changed;
    public string? RecoveryWarning => store.LoadError;
    public IReadOnlyList<DownloadTaskRecord> Tasks { get { lock (sync) return DownloadTaskIdentity.Copy(database.Tasks); } }
    public IReadOnlyList<DownloadOwnerRecord> Owners { get { lock (sync) return DownloadTaskIdentity.Copy(database.Owners); } }
    public DownloadTaskDatabase GetSnapshot() { lock (sync) return DownloadTaskIdentity.Copy(database); }
    public bool HasRunningTasks { get { lock (sync) return executions.Count > 0 || ownerExecutions.Count > 0; } }

    public DownloadTaskService(Downloads downloads, string stateDirectory, int maxConcurrent = 2, int maxDependencyConcurrent = 4,
        Func<DependencyDownloadIdentity, LauncherConfig, CancellationToken, Task<string[]>>? dependencySourceResolver = null)
    {
        if (maxConcurrent is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        if (maxDependencyConcurrent is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maxDependencyConcurrent));
        if (downloads.Dispatcher is not null) throw new InvalidOperationException("下载调度器已配置。");
        this.downloads = downloads;
        this.maxConcurrent = maxConcurrent;
        this.maxDependencyConcurrent = maxDependencyConcurrent;
        this.dependencySourceResolver = dependencySourceResolver ?? ((dependency, config, ct) =>
            DependencyPackageSources.RefreshSourcesAsync(dependency.Name, dependency.Version, dependency.FileName,
                dependency.Sha256, dependency.Size, config, ct));
        store = new(stateDirectory);
        database = store.Load();
        var recovered = false;
        foreach (var task in database.Tasks.Where(t => !DownloadTaskIdentity.Terminal(t.Status)))
        {
            var interrupted = task.Status is DownloadTaskStatus.Queued or DownloadTaskStatus.Running or DownloadTaskStatus.Pausing;
            if (task.CancellationRequested || interrupted)
            {
                task.Status = task.CancellationRequested ? DownloadTaskStatus.Canceled : DownloadTaskStatus.Paused;
                task.Phase = task.Status.ToString();
            }
            task.Speed = null; task.Connections = null; task.RetryAt = null;
            if (task.CancellationRequested) task.Error = "任务已取消，未完成文件保留。";
            else if (interrupted && string.IsNullOrWhiteSpace(task.Error)) task.Error = "上次下载尚未完成，点击继续后将检查已有文件和断点。";
            recovered = true;
        }
        foreach (var owner in database.Owners.Where(o => !DownloadTaskIdentity.Terminal(o.Status)))
        {
            var interrupted = owner.Status is DownloadTaskStatus.Queued or DownloadTaskStatus.Running or DownloadTaskStatus.Pausing;
            if (owner.CancellationRequested || interrupted)
            {
                owner.Status = owner.CancellationRequested ? DownloadTaskStatus.Canceled : DownloadTaskStatus.Paused;
                owner.Phase = owner.Status.ToString();
            }
            if (owner.CancellationRequested) owner.Error = "批次中的文件已取消；一键继续不会恢复此批次。";
            else if (interrupted && string.IsNullOrWhiteSpace(owner.Error)) owner.Error = "上次任务尚未完成，可手动继续原计划。";
            recovered = true;
        }
        if (recovered) Save();
        downloads.Dispatcher = this;
    }

    public DownloadOwnerRecord RegisterOwner(string kind, string title, LauncherConfig configuration,
        IEnumerable<ModelDownloadIntent>? models = null, JsonElement? payload = null, string? id = null)
    {
        var record = new DownloadOwnerRecord
        {
            Id = id ?? Guid.NewGuid().ToString("N"), Kind = kind, Title = title,
            EnvironmentRoot = string.IsNullOrWhiteSpace(configuration.ComfyRoot) ? "" : Path.GetFullPath(configuration.ComfyRoot),
            Configuration = RuntimePolicies.SnapshotConfig(configuration),
            Models = DownloadTaskIdentity.Copy(models?.ToArray() ?? []), Payload = payload?.Clone()
        };
        if (!Guid.TryParseExact(record.Id, "N", out _) || string.IsNullOrWhiteSpace(kind))
            throw new InvalidDataException("下载批次身份无效。");
        record.Configuration.Validate();
        DownloadTaskIdentity.ValidatePayload(JsonSerializer.SerializeToElement(record.Configuration, JsonFile.Options));
        foreach (var model in record.Models) DownloadTaskIdentity.ValidateModel(model);
        if (record.Models.Select(model => model.Model.Id).Distinct(StringComparer.Ordinal).Count() != record.Models.Length)
            throw new InvalidDataException("下载批次不能包含重复模型身份。");
        DownloadTaskIdentity.ValidatePayload(JsonSerializer.SerializeToElement(record.Models, JsonFile.Options));
        if (record.Payload is { } frozen) DownloadTaskIdentity.ValidatePayload(frozen);
        record.Fingerprint = DownloadTaskIdentity.Owner(record);
        lock (sync)
        {
            EnsureWritable();
            var existing = id is not null ? database.Owners.SingleOrDefault(o => o.Id == record.Id)
                : database.Owners.LastOrDefault(o => o.Fingerprint == record.Fingerprint && o.Status != DownloadTaskStatus.Canceled);
            if (existing is not null)
            {
                if (existing.Fingerprint != record.Fingerprint) throw new InvalidOperationException("此任务的版本或目标已经变化，请创建新的任务。");
                return DownloadTaskIdentity.Copy(existing);
            }
            database.Owners.Add(record);
            try { Save(); }
            catch { database.Owners.Remove(record); throw; }
        }
        Notify();
        return DownloadTaskIdentity.Copy(record);
    }

    /// <summary>Use inside an owner coordinator to tag each required/optional child. Scope flows across await.</summary>
    public IDisposable WithinOwner(string id, bool required = true)
    {
        lock (sync) { EnsureWritable(); _ = Owner(id); }
        var previous = context.Value;
        context.Value = new(id, required);
        return new Scope(() => context.Value = previous);
    }

    public async Task RunOwnerAsync(string id, Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        OwnerExecution execution;
        lock (sync)
        {
            EnsureWritable();
            if (ownerExecutions.ContainsKey(id)) throw new InvalidOperationException("此下载批次已经在运行。");
            var owner = Owner(id);
            if (owner.IsResourceGroup) throw new InvalidOperationException("此旧批次已按模型拆分，请继续其中的模型任务。");
            var previous = DownloadTaskIdentity.Copy(owner);
            execution = new(CancellationTokenSource.CreateLinkedTokenSource(ct));
            ownerExecutions.Add(id, execution);
            owner.Status = DownloadTaskStatus.Running; owner.Phase = "Running"; owner.Error = ""; owner.UpdatedAt = DateTimeOffset.UtcNow;
            owner.CancellationRequested = false;
            owner.CompletedModelIds.Clear(); // Revalidate completed files before counting them in a new attempt.
            try { Save(); }
            catch
            {
                database.Owners[database.Owners.IndexOf(owner)] = previous;
                ownerExecutions.Remove(id); execution.Cancellation.Dispose(); throw;
            }
        }
        Notify();
        try
        {
            using var scope = WithinOwner(id);
            await action(execution.Cancellation.Token).ConfigureAwait(false);
            // The coordinator must return only after all final validation/publication steps.
            lock (sync) SetOwnerState(id, execution.CancelRequested ? DownloadTaskStatus.Canceled : DownloadTaskStatus.Completed,
                execution.CancelRequested ? "任务已取消，已下载文件保留。" : "");
        }
        catch (OperationCanceledException) when (execution.Cancellation.IsCancellationRequested || execution.ChildInterrupted)
        {
            lock (sync) SetOwnerState(id, execution.CancelRequested ? DownloadTaskStatus.Canceled : DownloadTaskStatus.Paused,
                execution.CancelRequested ? "任务已取消，已下载文件保留。" : "任务已暂停，已下载文件保留。");
            throw;
        }
        catch (OperationCanceledException)
        {
            lock (sync) SetOwnerState(id, DownloadTaskStatus.Paused, "下载子任务已暂停，原计划及文件保留。");
            throw;
        }
        catch (Exception ex)
        {
            lock (sync) SetOwnerState(id, execution.CancelRequested ? DownloadTaskStatus.Canceled
                : ex is DownloadCheckpointException ? DownloadTaskStatus.NeedsReview : DownloadTaskStatus.Failed, SafeError(ex.Message));
            throw;
        }
        finally
        {
            lock (sync) ownerExecutions.Remove(id);
            execution.Cancellation.Dispose();
            execution.Finished.TrySetResult();
            Notify();
        }
    }

    public async Task FetchAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        request = DownloadTaskIdentity.Validate(request);
        Execution execution;
        var subscription = Guid.NewGuid().ToString("N");
        var current = context.Value;
        lock (sync)
        {
            EnsureWritable();
            // Ambient owner wins over a mutable global configuration resolver.
            request = request with { Network = current is not null ? NetworkPolicySnapshot.Capture(Owner(current.Id).Configuration)
                : request.Network ?? NetworkPolicySnapshot.Capture() };
            var fingerprint = DownloadTaskIdentity.Artifact(request);
            var target = DownloadTaskIdentity.PathKey(request.Destination);
            if (cleaningTargets.Contains(target)) throw new InvalidOperationException("此目标正在清理未完成文件，请等待清理结束后重试。");
            var conflict = database.Tasks.FirstOrDefault(t => DownloadTaskIdentity.PathKey(t.Destination) == target
                && t.Fingerprint != fingerprint && !DownloadTaskIdentity.Terminal(t.Status));
            if (conflict is not null) throw new InvalidOperationException("同一目标仍有其他版本的下载任务，请先取消旧任务：" + conflict.Name);
            var record = request.TaskId is { } taskId ? database.Tasks.SingleOrDefault(t => t.Id == taskId)
                : database.Tasks.LastOrDefault(t => t.Fingerprint == fingerprint);
            if (request.TaskId is not null && record is null) throw new InvalidOperationException("下载任务不存在，请重新确认。");
            if (record is not null && record.Fingerprint != fingerprint) throw new InvalidOperationException("禁止更改任务版本或目标后沿用断点。");
            if (record is not null && cleaning.Contains(record.Id)) throw new InvalidOperationException("此任务正在清理未完成文件，请等待清理结束。");
            if (record is null)
            {
                record = new() { Name = request.Name, Destination = request.Destination, Size = request.Size, Sha256 = request.Sha256,
                    Urls = request.Urls, Network = request.Network, PreferHttpClient = request.PreferHttpClient,
                    TransferProfile = request.TransferProfile, Dependency = request.Dependency,
                    Fingerprint = fingerprint, Required = current?.Required ?? true };
                database.Tasks.Add(record);
            }
            if (current is not null)
            {
                _ = Owner(current.Id);
                if (record.OwnerIds.Count > 0 && !record.OwnerIds.Contains(current.Id) && !executions.ContainsKey(record.Id)
                    && record.OwnerIds.Where(id => !Owner(id).IsResourceGroup).All(id => DownloadTaskIdentity.Terminal(Owner(id).Status)))
                {
                    // A new explicit owner after cancellation/completion uses its new
                    // transport snapshot while retaining the same verified content identity.
                    record.Urls = request.Urls; record.Network = request.Network;
                    // Compatibility is an explicit per-content recovery choice. A derived
                    // single-resource owner must retain it rather than silently undoing it.
                    record.PreferHttpClient |= request.PreferHttpClient; record.TransportRevision++;
                }
                if (!record.OwnerIds.Contains(current.Id)) record.OwnerIds.Add(current.Id);
            }
            var newExecution = !executions.TryGetValue(record.Id, out execution!);
            if (newExecution)
            {
                record.Status = DownloadTaskStatus.Queued; record.Phase = "Queued"; record.Error = "";
                record.CancellationRequested = false;
                record.AttemptId = Guid.NewGuid().ToString("N"); record.UpdatedAt = DateTimeOffset.UtcNow;
                execution = new(record.Id, record.AttemptId, ++sequence);
                executions.Add(record.Id, execution);
                pending.Add(execution);
            }
            execution.Subscribers.Add(subscription, new(current?.Id, progress, current?.Required ?? true));
            record.Required = execution.Subscribers.Values.Any(subscriber => subscriber.Required);
            try { Save(); }
            catch
            {
                execution.Subscribers.Remove(subscription);
                record.Required = execution.Subscribers.Values.Any(subscriber => subscriber.Required);
                if (newExecution)
                {
                    executions.Remove(record.Id); pending.Remove(execution); execution.Cancellation.Dispose();
                    record.Status = DownloadTaskStatus.Failed; record.Phase = "Failed";
                    record.Error = "任务记录未能保存，请检查磁盘后重试。";
                }
                throw;
            }
            if (newExecution) execution.Work = Task.Run(() => ExecuteAsync(execution));
            Pump();
        }
        Notify();
        try { await execution.Finished.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            var quiesce = false;
            lock (sync)
            {
                execution.Subscribers.Remove(subscription);
                if (execution.Subscribers.Count > 0)
                    TaskRecord(execution.Id).Required = execution.Subscribers.Values.Any(subscriber => subscriber.Required);
                if (execution.Subscribers.Count == 0 && !execution.Finished.Task.IsCompleted)
                {
                    execution.Cancellation.Cancel();
                    quiesce = true;
                }
            }
            // Do not expose Paused to the parent while the last writer still runs.
            if (quiesce) await execution.Work.ConfigureAwait(false);
        }
    }

    private void Pump()
    {
        // Dependency batches may use four file slots, while ordinary transfers retain
        // their existing two-file limit. Both classes still share the HTTP connection budget.
        while (running < Math.Max(maxConcurrent, maxDependencyConcurrent))
        {
            var active = executions.Values.Where(e => e.Started).ToArray();
            var dependencies = active.Count(e => TaskRecord(e.Id).TransferProfile == DownloadTransferProfile.Dependency);
            var ordinary = active.Length - dependencies;
            var next = pending.Where(e => !e.Cancellation.IsCancellationRequested
                    && (TaskRecord(e.Id).TransferProfile == DownloadTransferProfile.Dependency
                        ? dependencies < maxDependencyConcurrent : ordinary < maxConcurrent))
                .OrderByDescending(e => TaskRecord(e.Id).Required).ThenBy(e => e.Sequence).FirstOrDefault();
            if (next is null) return;
            pending.Remove(next); next.Started = true; running++;
            next.Start.TrySetResult();
        }
    }

    private async Task ExecuteAsync(Execution execution)
    {
        var succeeded = false;
        var canceled = false;
        Exception? failure = null;
        try
        {
            await execution.Start.Task.WaitAsync(execution.Cancellation.Token).ConfigureAwait(false);
            DownloadRequest request;
            lock (sync)
            {
                var record = TaskRecord(execution.Id);
                record.Status = DownloadTaskStatus.Running; record.Phase = "Connecting";
                record.Attempts++; record.SourceRetryCount = 0; record.FailureCode = null;
                request = record.Request(); Save();
            }
            Notify();
            await downloads.FetchDirectAsync(request, new InlineProgress(p => Report(execution, p)), execution.Cancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                var record = TaskRecord(execution.Id);
                record.Status = DownloadTaskStatus.Completed; record.Phase = "Completed"; record.Received = record.Size;
                record.Error = ""; record.RetryAt = null; record.Speed = null; record.Connections = null;
                record.UpdatedAt = DateTimeOffset.UtcNow; Save();
            }
            succeeded = true;
        }
        catch (OperationCanceledException) when (execution.Cancellation.IsCancellationRequested)
        {
            lock (sync)
            {
                var record = TaskRecord(execution.Id);
                record.Status = execution.CancelRequested ? DownloadTaskStatus.Canceled : DownloadTaskStatus.Paused;
                record.Phase = "Paused"; record.Speed = null; record.RetryAt = null; record.Connections = null;
                record.Error = execution.CancelRequested ? "任务已取消，未完成文件保留。" : "下载已暂停，未完成文件保留。";
                record.UpdatedAt = DateTimeOffset.UtcNow; Save();
                foreach (var subscriber in execution.Subscribers.Values)
                    if (subscriber.OwnerId is { } ownerId && ownerExecutions.TryGetValue(ownerId, out var owner)) owner.ChildInterrupted = true;
            }
            canceled = true;
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                var record = TaskRecord(execution.Id);
                record.Status = ex is DownloadCheckpointException ? DownloadTaskStatus.NeedsReview : DownloadTaskStatus.Failed;
                record.Phase = record.Status.ToString(); record.Error = SafeError(ex.Message);
                if (ex is DownloadCheckpointException)
                {
                    record.FailureCode = "checkpoint";
                    record.Error += " 可在下载页选择兼容模式重新下载，或确认删除此任务未完成文件后重试。";
                }
                record.Speed = null; record.RetryAt = null; record.Connections = null; record.UpdatedAt = DateTimeOffset.UtcNow;
                try { Save(); } catch { /* Preserve the original error; the store reports persistence failure on next operation. */ }
            }
            failure = ex;
        }
        finally
        {
            lock (sync)
            {
                executions.Remove(execution.Id); pending.Remove(execution);
                if (execution.Started) running--;
                Pump();
            }
            // Do not let immediate callers attach to a finished execution that still
            // occupies its destination/scheduler slot instead of starting validation.
            if (succeeded) execution.Finished.TrySetResult();
            else if (canceled) execution.Finished.TrySetCanceled(execution.Cancellation.Token);
            else execution.Finished.TrySetException(failure ?? new IOException("下载任务状态未能安全保存，请检查磁盘并重试。"));
            Notify();
        }
    }

    private void Report(Execution execution, DownloadProgress value)
    {
        IProgress<DownloadProgress>[] subscribers;
        lock (sync)
        {
            if (!executions.TryGetValue(execution.Id, out var active) || !ReferenceEquals(active, execution)
                || execution.Cancellation.IsCancellationRequested || TaskRecord(execution.Id).AttemptId != execution.AttemptId) return;
            var record = TaskRecord(execution.Id);
            if (record.Status != DownloadTaskStatus.Running) return;
            record.Received = Math.Clamp(value.Received, 0, record.Size); record.Phase = SafeError(value.Phase ?? value.Stage);
            if (value.Source is not null && SafeError(value.Source) != record.Source) record.SourceRetryCount = 0;
            if (value.Attempt is { } attempt) record.SourceRetryCount = Math.Max(0, attempt);
            record.Engine = value.Engine; record.Source = value.Source is null ? null : SafeError(value.Source); record.RetryAt = value.RetryAt;
            record.Speed = execution.Speed.Sample(value.Phase, value.Speed, execution.Clock.Elapsed);
            value = value with { Speed = record.Speed };
            record.Connections = value.Connections; record.FailureCode = value.FailureCode;
            record.UpdatedAt = DateTimeOffset.UtcNow;
            if (record.UpdatedAt - execution.LastSaved >= TimeSpan.FromSeconds(1)) { Save(); execution.LastSaved = record.UpdatedAt; }
            subscribers = execution.Subscribers.Values.Where(s => s.Progress is not null).Select(s => s.Progress!).ToArray();
        }
        var update = value with { TaskId = execution.Id };
        foreach (var subscriber in subscribers) subscriber.Report(update);
        Notify();
    }

    public Task PauseOwnerAsync(string id) => StopOwnerAsync(id, false);
    public Task CancelOwnerAsync(string id) => StopOwnerAsync(id, true);
    private async Task StopOwnerAsync(string id, bool cancel)
    {
        string[] resources;
        lock (sync) resources = Owner(id).ResourceOwnerIds.Values.Distinct(StringComparer.Ordinal).ToArray();
        if (resources.Length > 0)
        {
            await Task.WhenAll(resources.Select(resource => StopOwnerAsync(resource, cancel))).ConfigureAwait(false);
            return;
        }
        Task? wait = null;
        lock (sync)
        {
            EnsureWritable(allowClosing: true);
            var record = Owner(id);
            if (ownerExecutions.TryGetValue(id, out var active))
            {
                active.CancelRequested |= cancel; record.CancellationRequested |= cancel; record.Status = DownloadTaskStatus.Pausing;
                active.Cancellation.Cancel(); wait = active.Finished.Task; Save();
            }
            else if (record.Status != DownloadTaskStatus.Completed)
            {
                record.CancellationRequested |= cancel;
                SetOwnerState(id, record.CancellationRequested ? DownloadTaskStatus.Canceled : DownloadTaskStatus.Paused, "任务已停止，已下载文件保留。");
            }
        }
        Notify();
        if (wait is not null) await wait.ConfigureAwait(false);
    }

    public Task PauseTaskAsync(string id) => StopTaskAsync(id, false);
    public Task CancelTaskAsync(string id) => StopTaskAsync(id, true);
    private async Task StopTaskAsync(string id, bool cancel)
    {
        Task? wait = null;
        lock (sync)
        {
            EnsureWritable(allowClosing: true);
            var record = TaskRecord(id);
            if (cancel)
                foreach (var ownerId in record.OwnerIds)
                {
                    var owner = Owner(ownerId);
                    if (ownerExecutions.TryGetValue(ownerId, out var ownerExecution))
                    {
                        ownerExecution.CancelRequested = true;
                        ownerExecution.ChildInterrupted = true;
                        owner.CancellationRequested = true;
                    }
                    else if (owner.Status != DownloadTaskStatus.Completed)
                    {
                        owner.Status = DownloadTaskStatus.Canceled;
                        owner.CancellationRequested = true;
                        owner.Error = "批次中的文件已取消；一键继续不会恢复此批次。";
                    }
                }
            if (executions.TryGetValue(id, out var active))
            {
                active.CancelRequested |= cancel; record.CancellationRequested |= cancel; record.Status = DownloadTaskStatus.Pausing;
                active.Cancellation.Cancel(); wait = active.Work;
            }
            else if (record.Status != DownloadTaskStatus.Completed)
            {
                record.CancellationRequested |= cancel;
                record.Status = record.CancellationRequested ? DownloadTaskStatus.Canceled : DownloadTaskStatus.Paused;
                record.Phase = "Paused"; record.Error = "任务已停止，已下载文件保留。";
            }
            Save();
        }
        Notify();
        if (wait is not null) await wait.ConfigureAwait(false);
    }

    public async Task PauseAllAsync()
    {
        string[] owners; string[] files;
        lock (sync) { owners = ownerExecutions.Keys.ToArray(); files = executions.Keys.ToArray(); }
        await Task.WhenAll(owners.Select(PauseOwnerAsync).Concat(files.Select(PauseTaskAsync))).ConfigureAwait(false);
    }

    public Task ResumeTaskAsync(string id, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        DownloadRequest request;
        lock (sync)
        {
            var record = TaskRecord(id);
            if (record.OwnerIds.Any(ownerId => Owner(ownerId).Kind != "file"))
                throw new InvalidOperationException("此文件属于安装、模型包或更新计划，请从所属任务继续以完成最终验证。");
            request = record.Request();
        }
        return FetchAsync(request, progress, ct);
    }

    public async Task DeletePartialAsync(string id, CancellationToken ct = default)
    {
        bool completed;
        string target;
        lock (sync)
        {
            EnsureWritable(); EnsureCleanupAllowed(TaskRecord(id));
            if (!cleaning.Add(id)) throw new InvalidOperationException("此任务正在清理。");
            target = DownloadTaskIdentity.PathKey(TaskRecord(id).Destination);
            if (!cleaningTargets.Add(target)) { cleaning.Remove(id); throw new InvalidOperationException("此目标正在清理。"); }
            completed = TaskRecord(id).Status == DownloadTaskStatus.Completed;
        }
        try
        {
            if (!completed) await CancelTaskAsync(id).ConfigureAwait(false);
            DownloadRequest request;
            bool allowUnidentified;
            lock (sync)
            {
                var record = TaskRecord(id); EnsureCleanupAllowed(record);
                request = record.Request(); allowUnidentified = HasSingleTargetIdentity(record);
            }
            await Downloads.CancelPartialAsync(request.Destination, request.Size, request.Sha256, allowUnidentified, ct).ConfigureAwait(false);
            lock (sync)
            {
                var record = TaskRecord(id);
                if (!completed) { record.Received = 0; record.Status = DownloadTaskStatus.Canceled; }
                record.Error = "未完成下载文件已删除；已完成文件保持不变。"; Save();
            }
        }
        finally { lock (sync) { cleaning.Remove(id); cleaningTargets.Remove(target); } }
        Notify();
    }

    public DownloadCleanupPreview CleanupPreview(string id)
    {
        lock (sync)
        {
            var record = TaskRecord(id);
            EnsureCleanupAllowed(record);
            var paths = Downloads.PartialFiles(record.Destination).Where(File.Exists).ToArray();
            return new(id, paths, paths.Sum(DownloadDiskSpace.AllocatedBytes))
                { LogicalBytes = paths.Sum(path => new FileInfo(path).Length) };
        }
    }

    private void EnsureCleanupAllowed(DownloadTaskRecord record)
    {
        var target = DownloadTaskIdentity.PathKey(record.Destination);
        if (cleaningTargets.Contains(target) && !cleaning.Contains(record.Id))
            throw new InvalidOperationException("此目标正在清理，请等待当前操作结束。");
        if (database.Tasks.Any(other => other.Id != record.Id && other.Fingerprint != record.Fingerprint
            && DownloadTaskIdentity.PathKey(other.Destination) == target && !DownloadTaskIdentity.Terminal(other.Status)))
            throw new InvalidOperationException("同一目标有另一个未完成版本，不能从旧任务清理其断点。请先处理该版本的任务。");
        if (record.OwnerIds.Count(id => !Owner(id).IsResourceGroup && !DownloadTaskIdentity.Terminal(Owner(id).Status)) > 1)
            throw new InvalidOperationException("此下载由多个未完成批次共用。请先取消不再需要的批次，再清理未完成文件。");
        Downloads.ValidatePartialOwnership(record.Destination, record.Size, record.Sha256, HasSingleTargetIdentity(record));
    }

    private bool HasSingleTargetIdentity(DownloadTaskRecord record) => database.Tasks
        .Where(other => DownloadTaskIdentity.PathKey(other.Destination) == DownloadTaskIdentity.PathKey(record.Destination))
        .Select(other => other.Fingerprint).Distinct(StringComparer.Ordinal).Count() == 1;

    public void SetTaskCompatibilityMode(string id)
    {
        lock (sync)
        {
            EnsureWritable();
            if (executions.ContainsKey(id) || cleaning.Contains(id)) throw new InvalidOperationException("请先暂停任务，再选择兼容模式重新下载。");
            var record = TaskRecord(id);
            record.PreferHttpClient = true; record.TransportRevision++;
            record.Error = "已选择内置兼容引擎；原 aria2 断点保留，开始前会重新检查额外磁盘空间。";
            Save();
        }
        Notify();
    }

    public void SetOwnerPhase(string id, string phase, string detail)
    {
        lock (sync)
        {
            var owner = Owner(id); owner.Phase = SafeError(phase); owner.Detail = SafeError(detail); Save();
        }
        Notify();
    }

    /// <summary>The caller invokes this only after the complete model/package and its post-processing succeed.</summary>
    public void MarkModelCompleted(string ownerId, string modelId)
    {
        lock (sync)
        {
            EnsureWritable();
            var owner = Owner(ownerId);
            if (!ownerExecutions.TryGetValue(ownerId, out var run) || run.Cancellation.IsCancellationRequested
                || owner.Status != DownloadTaskStatus.Running) return;
            if (!owner.Models.Any(model => model.Model.Id == modelId)) throw new InvalidOperationException("模型不属于此固定下载批次。");
            if (!owner.CompletedModelIds.Contains(modelId)) owner.CompletedModelIds.Add(modelId);
            owner.UpdatedAt = DateTimeOffset.UtcNow; Save();
        }
        Notify();
    }

    public void UpdateTaskSources(string id, string[] urls, NetworkPolicySnapshot? network = null)
    {
        lock (sync)
        {
            EnsureWritable();
            if (executions.ContainsKey(id)) throw new InvalidOperationException("请先暂停任务，再更换来源。");
            var record = TaskRecord(id);
            var validated = DownloadTaskIdentity.Validate(record.Request() with { Urls = urls, Network = network ?? record.Network });
            record.Urls = validated.Urls; record.Network = network ?? record.Network; record.TransportRevision++;
            record.UpdatedAt = DateTimeOffset.UtcNow; Save();
        }
        Notify();
    }

    public DownloadOwnerRecord UpdateOwnerSources(string id, LauncherConfig sourceConfig)
    {
        DownloadOwnerRecord result;
        lock (sync)
        {
            if (database.Tasks.Any(task => task.OwnerIds.Contains(id) && task.Dependency is not null))
                throw new InvalidOperationException("此批次包含 Python 依赖，请使用异步换源检查以保留固定包身份。");
            result = UpdateOwnerSourcesLocked(id, sourceConfig, new Dictionary<string, string[]>());
        }
        Notify(); return result;
    }

    /// <summary>Resolve package mirrors without holding the store lock, then atomically replace only transport settings.</summary>
    public async Task<DownloadOwnerRecord> UpdateOwnerSourcesAsync(string id, LauncherConfig sourceConfig, CancellationToken ct = default)
    {
        DownloadOwnerRecord frozenOwner;
        DownloadTaskRecord[] frozenTasks;
        LauncherConfig next;
        lock (sync)
        {
            EnsureWritable();
            frozenOwner = DownloadTaskIdentity.Copy(Owner(id));
            frozenTasks = DownloadTaskIdentity.Copy(database.Tasks.Where(task => task.OwnerIds.Contains(id)).ToArray());
            EnsureSourcesStopped(id, frozenTasks);
            next = SourceConfiguration(frozenOwner.Configuration, sourceConfig);
        }
        using var gate = new SemaphoreSlim(4, 4);
        var sources = await Task.WhenAll(frozenTasks.Where(task => task.Dependency is not null).Select(async task =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var dependency = task.Dependency!;
                var urls = await dependencySourceResolver(dependency, next, ct).ConfigureAwait(false);
                return (task.Id, Urls: urls.Length > 0 ? urls : task.Urls);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        DownloadOwnerRecord result;
        lock (sync)
        {
            EnsureWritable();
            var current = database.Tasks.Where(task => task.OwnerIds.Contains(id)).ToArray();
            EnsureSourcesStopped(id, current);
            if (Owner(id).Fingerprint != frozenOwner.Fingerprint || Owner(id).TransportRevision != frozenOwner.TransportRevision
                || current.Length != frozenTasks.Length || frozenTasks.Any(before => !current.Any(after => after.Id == before.Id
                    && after.Fingerprint == before.Fingerprint && after.TransportRevision == before.TransportRevision && after.AttemptId == before.AttemptId)))
                throw new InvalidOperationException("换源检查期间任务已变化，请保持暂停后重新检查。");
            result = UpdateOwnerSourcesLocked(id, next, sources.ToDictionary(item => item.Id, item => item.Urls));
        }
        Notify(); return result;
    }

    private void EnsureSourcesStopped(string id, IEnumerable<DownloadTaskRecord> tasks)
    {
        if (ownerExecutions.ContainsKey(id)) throw new InvalidOperationException("请先暂停此批次，再选择换源继续。");
        if (Owner(id).IsResourceGroup) throw new InvalidOperationException("请先选择此批次中的模型，再更换下载来源。");
        if (tasks.Any(task => executions.ContainsKey(task.Id))) throw new InvalidOperationException("共享文件仍在下载，请先暂停后换源。");
    }

    private static LauncherConfig SourceConfiguration(LauncherConfig original, LauncherConfig sourceConfig)
    {
        var next = original.Clone();
        next.SourceId = sourceConfig.SourceId; next.GitMirror = sourceConfig.GitMirror;
        next.ModelMirror = sourceConfig.ModelMirror; next.PipIndex = sourceConfig.PipIndex;
        next.NetworkMode = sourceConfig.NetworkMode; next.ProxyAddress = sourceConfig.ProxyAddress;
        next.Validate();
        DownloadTaskIdentity.ValidatePayload(JsonSerializer.SerializeToElement(next, JsonFile.Options));
        return next;
    }

    private DownloadOwnerRecord UpdateOwnerSourcesLocked(string id, LauncherConfig sourceConfig, IReadOnlyDictionary<string, string[]> packageSources)
    {
        EnsureWritable();
        var owner = Owner(id);
        var tasks = database.Tasks.Where(task => task.OwnerIds.Contains(id)).ToArray();
        EnsureSourcesStopped(id, tasks);
        var next = SourceConfiguration(owner.Configuration, sourceConfig);
        var network = NetworkPolicySnapshot.Capture(next);
        var updates = new Dictionary<string, string[]>();
        foreach (var record in tasks)
        {
            string[]? urls = packageSources.TryGetValue(record.Id, out var refreshed) ? refreshed : null;
            foreach (var intent in owner.Models)
            {
                if (!intent.Model.IsPackage && DownloadTaskIdentity.PathKey(intent.Destination) == DownloadTaskIdentity.PathKey(record.Destination))
                    urls = DownloadSources.ModelCandidates(intent.Model, next);
                else if (intent.Model.IsPackage)
                    foreach (var file in intent.Model.Files)
                        if (DownloadTaskIdentity.PathKey(Safety.Under(ModelPackages.Staging(intent.Model, intent.Destination), file.Path)) == DownloadTaskIdentity.PathKey(record.Destination))
                            urls = DownloadSources.ModelCandidates(file, next);
            }
            if (record.Dependency is not null && urls is null) throw new InvalidOperationException("依赖包来源尚未完成身份检查。");
            updates[record.Id] = DownloadTaskIdentity.Validate(record.Request() with
                { Urls = urls ?? DownloadSources.Candidates(record.Urls[^1], next), Network = network }).Urls;
        }
        var previousOwner = DownloadTaskIdentity.Copy(owner);
        var previousTasks = DownloadTaskIdentity.Copy(tasks);
        owner.Configuration = next; owner.TransportRevision++; owner.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var update in updates) { var record = TaskRecord(update.Key); record.Urls = update.Value; record.Network = network; record.TransportRevision++; }
        try { Save(); }
        catch
        {
            database.Owners[database.Owners.IndexOf(owner)] = previousOwner;
            foreach (var previous in previousTasks) database.Tasks[database.Tasks.FindIndex(task => task.Id == previous.Id)] = previous;
            throw;
        }
        return DownloadTaskIdentity.Copy(owner);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed || closing) return; closing = true; }
        await PauseAllAsync().ConfigureAwait(false);
        lock (sync) { disposed = true; if (ReferenceEquals(downloads.Dispatcher, this)) downloads.Dispatcher = null; }
    }

    private void SetOwnerState(string id, DownloadTaskStatus status, string error)
    {
        var owner = Owner(id); owner.Status = status; owner.Phase = status.ToString(); owner.Error = error; owner.UpdatedAt = DateTimeOffset.UtcNow; Save();
    }
    private DownloadTaskRecord TaskRecord(string id) => database.Tasks.SingleOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException("下载任务不存在。");
    private DownloadOwnerRecord Owner(string id) => database.Owners.SingleOrDefault(o => o.Id == id) ?? throw new KeyNotFoundException("下载批次不存在。");
    private void EnsureWritable(bool allowClosing = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (closing && !allowClosing) throw new InvalidOperationException("下载服务正在保存并关闭，不能开始新的任务。");
        if (store.LoadError is not null) throw new IOException(store.LoadError);
    }
    private void Save() { RefreshResourceGroups(); store.Save(database); }
    private void Notify() { try { Changed?.Invoke(); } catch { /* A detached UI cannot corrupt a transfer. */ } }
    private static string SafeError(string message)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(message, @"https?://\S+", "[来源地址]");
        return System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(authorization|token|api[_-]?key|password|secret)\s*[:=]\s*\S+", "$1=[已隐藏]");
    }
    private sealed record OwnerContext(string Id, bool Required);
    private sealed record Subscriber(string? OwnerId, IProgress<DownloadProgress>? Progress, bool Required);
    private sealed class Scope(Action release) : IDisposable { private Action? action = release; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }
    private sealed class InlineProgress(Action<DownloadProgress> callback) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => callback(value); }
    private sealed class OwnerExecution(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancelRequested;
        public bool ChildInterrupted;
    }
    private sealed class Execution(string id, string attemptId, long sequence)
    {
        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
        public DownloadSpeedSmoother Speed { get; } = new();
        public string Id { get; } = id;
        public string AttemptId { get; } = attemptId;
        public long Sequence { get; } = sequence;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, Subscriber> Subscribers { get; } = new(StringComparer.Ordinal);
        public Task Work { get; set; } = Task.CompletedTask;
        public DateTimeOffset LastSaved;
        public bool Started;
        public bool CancelRequested;
    }
}
